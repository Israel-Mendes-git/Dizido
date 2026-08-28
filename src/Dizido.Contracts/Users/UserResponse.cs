namespace Dizido.Contracts.Users;

public sealed record UserResponse(
    Guid Id,
    string DisplayName,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

public sealed record CreateUserRequest(string DisplayName);
