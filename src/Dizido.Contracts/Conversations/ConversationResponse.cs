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
    bool IsOnline);

public sealed record CreateGroupRequest(string Title);

public sealed record CreateDirectRequest(Guid OtherUserId);
