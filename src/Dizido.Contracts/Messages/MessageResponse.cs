namespace Dizido.Contracts.Messages;

/// <summary>Uma mensagem como o cliente a enxerga.</summary>
/// <remarks>
/// Este DTO é deliberadamente diferente da entidade <c>Message</c> do domínio. Devolver a
/// entidade direto acopla a API ao modelo interno: qualquer renomeação de campo viraria uma
/// quebra de contrato para todos os clientes já instalados. O DTO é a fronteira estável.
/// </remarks>
public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderDisplayName,
    string Body,
    Guid ClientMessageId,
    Guid? ReplyToMessageId,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,

    /// <summary>"Text" ou "System". Avisos do sistema são renderizados como uma linha
    /// centralizada, sem balão nem avatar.</summary>
    string Kind = "Text",

    /// <summary>Em avisos do sistema: o código do que aconteceu (MemberJoined, TitleChanged...).</summary>
    string? SystemEvent = null,

    /// <summary>Em avisos do sistema: quem foi afetado.</summary>
    Guid? SystemTargetId = null,

    /// <summary>Nome de quem foi afetado, para a interface não precisar procurar.</summary>
    string? SystemTargetName = null,

    /// <summary>O arquivo que acompanha a mensagem, com URLs já assinadas.</summary>
    /// <remarks>
    /// Vem junto, e não por uma segunda chamada, para o balão renderizar de uma vez. As URLs
    /// aqui dentro têm prazo — ao expirar, o cliente pede um anexo novo em
    /// <c>GET /api/attachments/{id}</c>.
    /// </remarks>
    Attachments.AttachmentResponse? Attachment = null,

    /// <summary>Um resumo da mensagem que esta responde.</summary>
    /// <remarks>
    /// O <c>ReplyToMessageId</c> sozinho não basta para desenhar a citação: a mensagem citada
    /// pode estar centenas de páginas atrás no histórico, fora de tudo que o cliente carregou.
    /// Buscá-la sob demanda seria uma ida à rede por balão. O servidor manda o pedaço pronto.
    /// </remarks>
    MessageReplyPreview? ReplyTo = null);

/// <summary>O suficiente para desenhar a citação acima de uma resposta.</summary>
/// <param name="Excerpt">
/// Um trecho curto do original. Para mensagem com anexo e sem legenda, uma descrição do tipo
/// de arquivo — a citação precisa dizer alguma coisa.
/// </param>
public sealed record MessageReplyPreview(
    Guid MessageId,
    string SenderDisplayName,
    string Excerpt,
    bool IsDeleted);
