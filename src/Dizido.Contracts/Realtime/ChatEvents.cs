namespace Dizido.Contracts.Realtime;

/// <summary>Alguém começou (ou parou) de digitar numa conversa.</summary>
/// <remarks>
/// Evento efêmero: não é gravado em lugar nenhum. Se o destinatário estiver offline, ele
/// simplesmente não recebe — e não faz falta.
/// </remarks>
public sealed record TypingEvent(Guid ConversationId, Guid UserId, string DisplayName, bool IsTyping);

/// <summary>Um usuário ficou online ou offline.</summary>
public sealed record PresenceEvent(Guid UserId, bool IsOnline, DateTimeOffset At);

/// <summary>Um membro avançou sua marca d'água de leitura.</summary>
public sealed record ReadReceiptEvent(Guid ConversationId, Guid UserId, Guid LastReadMessageId);

/// <summary>Uma mensagem foi apagada.</summary>
public sealed record MessageDeletedEvent(Guid ConversationId, Guid MessageId, DateTimeOffset At);
