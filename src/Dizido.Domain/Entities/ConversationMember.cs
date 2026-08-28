using Dizido.Domain.Enums;

namespace Dizido.Domain.Entities;

/// <summary>
/// Participação de um usuário numa conversa. A chave primária é o par
/// (<see cref="ConversationId"/>, <see cref="UserId"/>) — não existe Id próprio,
/// porque a mesma pessoa não pode ser membro da mesma conversa duas vezes, e a chave
/// composta faz o banco garantir isso sozinho.
/// </summary>
public sealed class ConversationMember
{
    private ConversationMember() { }

    public Guid ConversationId { get; private set; }

    public Guid UserId { get; private set; }

    public MemberRole Role { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    /// <summary>Quando saiu (ou foi removido). Null enquanto é membro ativo.</summary>
    /// <remarks>
    /// Saída é registrada, não apagada: sem isso, as mensagens antigas ficariam com um
    /// remetente que não é membro de nada, e o histórico perderia sentido.
    /// </remarks>
    public DateTimeOffset? LeftAt { get; private set; }

    /// <summary>
    /// Marca d'água de leitura: a última mensagem que este membro leu.
    /// Tudo com Id menor ou igual a este está lido.
    /// </summary>
    /// <remarks>
    /// Guardar um recibo por (mensagem × membro) daria N×M linhas — um grupo de 50 pessoas
    /// com 10 mil mensagens viraria 500 mil registros. Uma marca por membro dá a mesma
    /// informação em 50 linhas, porque os Ids são UUIDv7 e portanto ordenados no tempo.
    /// </remarks>
    public Guid? LastReadMessageId { get; private set; }

    public DateTimeOffset? MutedUntil { get; private set; }

    public bool IsActive => LeftAt is null;

    internal static ConversationMember Create(
        Guid conversationId,
        Guid userId,
        MemberRole role,
        DateTimeOffset now) => new()
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAt = now,
        };

    internal void ChangeRole(MemberRole role) => Role = role;

    internal void Leave(DateTimeOffset now)
    {
        DomainException.Require(IsActive, "Este membro já saiu da conversa.");
        LeftAt = now;
    }

    internal void Rejoin(DateTimeOffset now)
    {
        LeftAt = null;
        JoinedAt = now;
    }

    /// <summary>Avança a marca de leitura. Nunca retrocede.</summary>
    public void MarkReadUpTo(Guid messageId)
    {
        // Comparar UUIDv7 como string hexadecimal ordena por tempo de criação.
        // (O CompareTo nativo de Guid no .NET compara campo a campo, e NÃO segue
        // a ordem dos bytes — por isso a comparação passa pelo formato "N".)
        if (LastReadMessageId is null ||
            string.CompareOrdinal(messageId.ToString("N"), LastReadMessageId.Value.ToString("N")) > 0)
        {
            LastReadMessageId = messageId;
        }
    }

    public void MuteUntil(DateTimeOffset? until) => MutedUntil = until;
}
