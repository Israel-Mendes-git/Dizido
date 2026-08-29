namespace Dizido.Contracts.Conversations;

public sealed record ConversationResponse(
    Guid Id,
    string Type,
    string? Title,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMessageAt,
    IReadOnlyList<ConversationMemberResponse> Members);

public sealed record ConversationMemberResponse(
    Guid UserId,
    string DisplayName,
    string Role,
    Guid? LastReadMessageId,
    bool IsOnline,

    /// <summary>Até quando este membro silenciou a conversa. Nulo se não silenciou.</summary>
    /// <remarks>
    /// <para>
    /// Vem por membro, e não só para quem pediu, porque a resposta é a mesma para todo mundo —
    /// e é o que o cliente já tem em mãos ao montar a lista. O interessado procura a própria
    /// linha.
    /// </para>
    /// <para>
    /// Silenciar é preferência pessoal e não é segredo de ninguém: saber que um colega
    /// silenciou o grupo não dá acesso a nada. Se um dia incomodar, é só filtrar no servidor.
    /// </para>
    /// </remarks>
    DateTimeOffset? MutedUntil = null);

public sealed record CreateGroupRequest(string Title);

public sealed record CreateDirectRequest(Guid OtherUserId);
