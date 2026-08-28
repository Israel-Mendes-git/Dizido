using Dizido.Domain.Enums;

namespace Dizido.Domain.Entities;

/// <summary>Uma mensagem dentro de uma conversa: texto de alguém ou aviso do sistema.</summary>
public sealed class Message
{
    public const int MaxBodyLength = 4000;

    private Message() { }

    /// <summary>
    /// UUIDv7: identificador único que também carrega o instante de criação nos bits
    /// iniciais, o que o torna ordenável no tempo.
    /// </summary>
    /// <remarks>
    /// Duas consequências práticas:
    /// <list type="number">
    /// <item>Os inserts entram no fim do índice B-tree do Postgres, em vez de espalhados —
    /// com UUID aleatório (v4) o índice fragmenta e as escritas ficam mais lentas conforme
    /// a tabela cresce.</item>
    /// <item>Ordenar por Id já ordena por tempo, com precisão maior que a de um timestamp:
    /// duas mensagens gravadas no mesmo milissegundo empatariam em SentAt, mas nunca no Id.</item>
    /// </list>
    /// </remarks>
    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>
    /// Quem escreveu — ou, em avisos do sistema, quem provocou o evento.
    /// </summary>
    public Guid SenderId { get; private set; }

    public string Body { get; private set; } = null!;

    /// <summary>
    /// Identificador gerado pelo cliente ANTES de enviar. Existe para deduplicação.
    /// </summary>
    /// <remarks>
    /// A rede vai cair no meio de um envio, e o cliente não tem como saber se o servidor
    /// gravou antes de a conexão morrer. Ele reenvia. Sem este campo, aparece mensagem
    /// duplicada. Com ele — e um índice único em (SenderId, ClientMessageId) — a segunda
    /// tentativa devolve a mensagem que já existe, em vez de criar outra.
    /// </remarks>
    public Guid ClientMessageId { get; private set; }

    public Guid? ReplyToMessageId { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    public DateTimeOffset? EditedAt { get; private set; }

    /// <summary>
    /// Quando foi apagada. A linha continua no banco (soft delete).
    /// </summary>
    /// <remarks>
    /// Apagar de verdade quebraria as respostas que apontam para ela e as marcas de leitura
    /// dos membros. Mantemos a linha e limpamos o corpo.
    /// </remarks>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Texto de uma pessoa ou aviso do sistema.</summary>
    public MessageKind Kind { get; private set; } = MessageKind.Text;

    /// <summary>Em avisos do sistema: o que aconteceu.</summary>
    public SystemEventKind? SystemEvent { get; private set; }

    /// <summary>Em avisos do sistema: quem foi afetado (removido, promovido...).</summary>
    public Guid? SystemTargetId { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public bool IsEdited => EditedAt is not null;

    public bool IsSystem => Kind == MessageKind.System;

    internal static Message Create(
        Guid conversationId,
        Guid senderId,
        string body,
        Guid clientMessageId,
        DateTimeOffset now,
        Guid? replyToMessageId = null)
    {
        ValidateBody(body);

        DomainException.Require(
            clientMessageId != Guid.Empty,
            "O cliente precisa enviar um ClientMessageId para permitir deduplicação.");

        return new Message
        {
            Id = Guid.CreateVersion7(now),
            ConversationId = conversationId,
            SenderId = senderId,
            Body = body.Trim(),
            ClientMessageId = clientMessageId,
            ReplyToMessageId = replyToMessageId,
            SentAt = now,
            Kind = MessageKind.Text,
        };
    }

    /// <summary>
    /// Cria um aviso do sistema no fluxo da conversa ("Fulano entrou", "o título mudou").
    /// </summary>
    /// <remarks>
    /// <para>
    /// O <c>SenderId</c> é quem provocou o evento, não um usuário fictício "sistema". Assim a
    /// interface consegue montar "Ana removeu Bruno" sem uma tabela de auditoria separada.
    /// </para>
    /// <para>
    /// Guardamos o código do evento e o alvo, não a frase pronta: a tradução acontece na
    /// interface de quem lê, e o texto pode ser reescrito sem migrar dados antigos.
    /// </para>
    /// </remarks>
    internal static Message CreateSystem(
        Guid conversationId,
        Guid actorId,
        SystemEventKind evento,
        DateTimeOffset now,
        Guid? targetId = null,
        string body = "") => new()
        {
            Id = Guid.CreateVersion7(now),
            ConversationId = conversationId,
            SenderId = actorId,
            Body = body,

            // Avisos do sistema não vêm de um cliente, mas o índice único
            // (SenderId, ClientMessageId) exige um valor. Um Guid novo garante unicidade.
            ClientMessageId = Guid.NewGuid(),

            SentAt = now,
            Kind = MessageKind.System,
            SystemEvent = evento,
            SystemTargetId = targetId,
        };

    public void Edit(Guid editorId, string body, DateTimeOffset now)
    {
        DomainException.Require(!IsSystem, "Avisos do sistema não podem ser editados.");
        DomainException.Require(editorId == SenderId, "Só o autor pode editar a própria mensagem.");
        DomainException.Require(!IsDeleted, "Mensagem apagada não pode ser editada.");

        ValidateBody(body);

        Body = body.Trim();
        EditedAt = now;
    }

    public void Delete(Guid requesterId, DateTimeOffset now, bool isModerator = false)
    {
        DomainException.Require(!IsSystem, "Avisos do sistema não podem ser apagados.");

        DomainException.Require(
            requesterId == SenderId || isModerator,
            "Só o autor ou um administrador pode apagar a mensagem.");

        if (IsDeleted)
        {
            return; // Idempotente: apagar duas vezes não é erro.
        }

        Body = string.Empty;
        DeletedAt = now;
    }

    private static void ValidateBody(string body)
    {
        DomainException.Require(
            !string.IsNullOrWhiteSpace(body),
            "A mensagem não pode ser vazia.");

        DomainException.Require(
            body.Trim().Length <= MaxBodyLength,
            $"A mensagem não pode passar de {MaxBodyLength} caracteres.");
    }
}
