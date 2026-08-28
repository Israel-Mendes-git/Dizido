namespace Dizido.Contracts.Conversations;

public sealed record RenameGroupRequest(string Title);

public sealed record ChangeRoleRequest(string Role);

public sealed record MuteRequest(DateTimeOffset? Until);
