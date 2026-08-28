using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;

namespace Dizido.Contracts.Sync;

/// <summary>Até onde o cliente já leu de uma conversa.</summary>
public sealed record ConversationCursor(Guid ConversationId, Guid? LastMessageId);

/// <summary>
/// "Estou desatualizado até aqui — me manda o que perdi."
/// </summary>
/// <remarks>
/// Enviado ao reconectar. Enquanto o WebSocket esteve caído, mensagens foram gravadas e os
/// eventos correspondentes se perderam — o SignalR não guarda o que não conseguiu entregar.
/// Sem esta chamada, o cliente ficaria com um buraco no histórico até o usuário reabrir a
/// conversa (ou nunca perceber, no caso de uma conversa que ele não abriu).
/// </remarks>
public sealed record SyncRequest(IReadOnlyList<ConversationCursor> Conversations);

/// <param name="Conversations">Todas as conversas do usuário, com o estado atual.</param>
/// <param name="Messages">Mensagens gravadas depois dos cursores informados.</param>
/// <param name="Truncated">
/// Conversas em que havia mais mensagens do que o limite por resposta. O cliente precisa
/// buscar o restante pela paginação normal em vez de assumir que recebeu tudo.
/// </param>
public sealed record SyncResponse(
    IReadOnlyList<ConversationResponse> Conversations,
    IReadOnlyList<MessageResponse> Messages,
    IReadOnlyList<Guid> Truncated);
