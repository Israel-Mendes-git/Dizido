using Dizido.Domain.Enums;

namespace Dizido.Domain.Entities;

/// <summary>
/// Um arquivo enviado para uma conversa: imagem exibida no balão ou anexo para download.
/// </summary>
/// <remarks>
/// <para>
/// É uma entidade própria, e não um punhado de colunas dentro de <see cref="Message"/>, porque
/// o arquivo <b>existe antes da mensagem</b>. O fluxo é: pedir permissão de upload, subir os
/// bytes direto para o storage, o servidor conferir, e só então enviar a mensagem que aponta
/// para ele. Se o usuário desistir no meio, sobra um anexo órfão — e não uma mensagem quebrada.
/// </para>
/// <para>
/// O mesmo modelo serve para o avatar do grupo, que também é um arquivo sem mensagem nenhuma.
/// </para>
/// </remarks>
public sealed class Attachment
{
    /// <summary>10 MB. Uma foto de celular moderno cabe; um vídeo, não.</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>50 MB. Acima disso o upload em uma requisição só começa a falhar em rede ruim.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    public const int MaxFileNameLength = 200;

    /// <summary>
    /// Os únicos formatos aceitos como imagem — e, portanto, os únicos que o cliente exibe
    /// dentro da página.
    /// </summary>
    /// <remarks>
    /// Lista de permissão, e não de bloqueio. Uma lista de bloqueio erra por omissão: basta
    /// alguém lembrar de um formato que o navegador saiba interpretar e que ninguém listou
    /// para o buraco abrir. Aqui, o que não está escrito abaixo é tratado como arquivo comum —
    /// o comportamento seguro é o padrão.
    /// </remarks>
    public static readonly IReadOnlySet<string> ImagensPermitidas =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/gif",
            "image/webp",
        };

    private Attachment() { }

    public Guid Id { get; private set; }

    /// <summary>A conversa a que o arquivo pertence. É o que decide quem pode baixá-lo.</summary>
    /// <remarks>
    /// A alternativa seria autorizar pela mensagem que o referencia. Não serve: entre o upload
    /// e o envio da mensagem não existe mensagem nenhuma, e o avatar do grupo nunca vai ter uma.
    /// Amarrando o anexo à conversa desde o pedido, a pergunta "esta pessoa pode ver isto?" é
    /// sempre a mesma — e é a que a <see cref="Conversation"/> já sabe responder.
    /// </remarks>
    public Guid ConversationId { get; private set; }

    public Guid UploadedById { get; private set; }

    /// <summary>Nome original, usado só no download. Nunca compõe o caminho no storage.</summary>
    public string FileName { get; private set; } = null!;

    /// <summary>
    /// O tipo confirmado pelo servidor depois de olhar os bytes — não o que o cliente disse.
    /// </summary>
    public string ContentType { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    public AttachmentKind Kind { get; private set; }

    public AttachmentStatus Status { get; private set; }

    /// <summary>Caminho do objeto dentro do bucket.</summary>
    /// <remarks>
    /// Derivado só de identificadores, sem um caractere sequer do nome enviado pelo usuário.
    /// Um nome como <c>../../etc/senha</c> não tem por onde virar caminho: o nome original
    /// mora numa coluna, e a chave é montada aqui.
    /// </remarks>
    public string StorageKey { get; private set; } = null!;

    /// <summary>Miniatura, só para imagens.</summary>
    public string? ThumbnailKey { get; private set; }

    /// <summary>Dimensões da imagem original, em pixels.</summary>
    /// <remarks>
    /// Guardadas para o cliente reservar o espaço do balão antes de a imagem carregar. Sem
    /// isso, o fluxo da conversa "pula" quando cada imagem termina de baixar, e quem estava
    /// lendo perde a linha.
    /// </remarks>
    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadyAt { get; private set; }

    public bool IsReady => Status == AttachmentStatus.Ready;

    /// <summary>
    /// Primeiro passo: registra a intenção de subir um arquivo e reserva o caminho no storage.
    /// </summary>
    /// <param name="declaredContentType">
    /// O que o cliente afirma estar enviando. Serve só para escolher o limite de tamanho e a
    /// expectativa de formato — a palavra final é dos bytes, em <see cref="Confirm"/>.
    /// </param>
    public static Attachment Request(
        Guid conversationId,
        Guid uploadedById,
        string fileName,
        string declaredContentType,
        long sizeBytes,
        DateTimeOffset now)
    {
        var nome = SanitizeFileName(fileName);
        var tipo = (declaredContentType ?? string.Empty).Trim().ToLowerInvariant();

        var kind = ImagensPermitidas.Contains(tipo) ? AttachmentKind.Image : AttachmentKind.File;

        ValidateSize(sizeBytes, kind);

        var id = Guid.CreateVersion7(now);

        return new Attachment
        {
            Id = id,
            ConversationId = conversationId,
            UploadedById = uploadedById,
            FileName = nome,

            // Arquivo comum entra como octet-stream mesmo que o cliente diga outra coisa.
            // O tipo declarado não vale nada até os bytes serem conferidos, e para o que não
            // é imagem nem chegamos a conferir — vai para download, e ponto.
            ContentType = kind == AttachmentKind.Image ? tipo : "application/octet-stream",

            SizeBytes = sizeBytes,
            Kind = kind,
            Status = AttachmentStatus.Pending,
            StorageKey = $"conversas/{conversationId:N}/{id:N}",
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Terceiro passo: os bytes chegaram, foram conferidos, e o anexo passa a valer.
    /// </summary>
    /// <param name="contentTypeReal">O tipo deduzido dos bytes iniciais do arquivo.</param>
    /// <param name="sizeReal">O tamanho que o storage relata — não o que o cliente prometeu.</param>
    public void Confirm(
        string contentTypeReal,
        long sizeReal,
        DateTimeOffset now,
        int? width = null,
        int? height = null,
        string? thumbnailKey = null)
    {
        DomainException.Require(
            Status == AttachmentStatus.Pending,
            "Este anexo já foi confirmado.");

        ValidateSize(sizeReal, Kind);

        if (Kind == AttachmentKind.Image)
        {
            // O cliente pediu para subir uma imagem e subiu outra coisa. Recusar aqui é o que
            // impede um arquivo qualquer de acabar exibido pelo navegador dentro da origem do
            // app — que é como um "gif" com HTML dentro vira XSS armazenado.
            DomainException.Require(
                ImagensPermitidas.Contains(contentTypeReal),
                "O conteúdo enviado não é uma imagem em formato aceito.");

            DomainException.Require(
                width is > 0 && height is > 0,
                "Imagem sem dimensões legíveis.");

            ContentType = contentTypeReal;
            Width = width;
            Height = height;
            ThumbnailKey = thumbnailKey;
        }

        // O tamanho vem do storage, e não do pedido: o cliente prometeu 2 MB na hora de pedir
        // a URL, mas nada o obrigava a cumprir a promessa ao fazer o PUT.
        SizeBytes = sizeReal;
        Status = AttachmentStatus.Ready;
        ReadyAt = now;
    }

    private static void ValidateSize(long sizeBytes, AttachmentKind kind)
    {
        DomainException.Require(sizeBytes > 0, "Arquivo vazio.");

        var limite = kind == AttachmentKind.Image ? MaxImageBytes : MaxFileBytes;
        var oQue = kind == AttachmentKind.Image ? "imagens" : "arquivos";

        DomainException.Require(
            sizeBytes <= limite,
            $"O arquivo passa do limite de {limite / (1024 * 1024)} MB para {oQue}.");
    }

    /// <summary>
    /// Reduz o nome enviado ao que é seguro guardar e devolver.
    /// </summary>
    /// <remarks>
    /// Ele não vira caminho no storage, mas ainda viaja no cabeçalho <c>Content-Disposition</c>
    /// do download e aparece na tela. Tirar separadores de caminho e caracteres de controle
    /// evita tanto um nome que confunde o sistema de arquivos de quem baixa quanto um que
    /// quebre o cabeçalho HTTP com uma quebra de linha no meio.
    /// </remarks>
    private static string SanitizeFileName(string fileName)
    {
        DomainException.Require(
            !string.IsNullOrWhiteSpace(fileName),
            "O arquivo precisa de um nome.");

        var limpo = new string([.. fileName
            .Trim()
            .Where(c => !char.IsControl(c) && c is not '/' and not '\\')]);

        DomainException.Require(
            limpo.Trim('.').Length > 0,
            "Nome de arquivo inválido.");

        return limpo.Length <= MaxFileNameLength ? limpo : limpo[..MaxFileNameLength];
    }
}
