namespace Dizido.Contracts.Conversations;

public sealed record RenameGroupRequest(string Title);

public sealed record ChangeRoleRequest(string Role);

public sealed record MuteRequest(DateTimeOffset? Until);

/// <summary>Troca a imagem do grupo. Nulo remove.</summary>
/// <param name="AttachmentId">
/// Um anexo já enviado e confirmado, da mesma conversa, pela mesma pessoa. O servidor não
/// aceita URL: ela expiraria, e o avatar quebraria no dia seguinte.
/// </param>
public sealed record SetGroupAvatarRequest(Guid? AttachmentId);
