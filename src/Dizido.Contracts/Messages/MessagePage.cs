namespace Dizido.Contracts.Messages;

/// <summary>Uma página de mensagens, da mais recente para a mais antiga.</summary>
/// <param name="Items">As mensagens da página.</param>
/// <param name="NextCursor">
/// Id da mensagem a partir da qual buscar a próxima página (mais antiga).
/// Null quando não há mais nada para carregar.
/// </param>
/// <remarks>
/// Paginação por cursor, e não por número de página. Numa lista que cresce pelo topo,
/// `OFFSET 50` significa "pule as 50 mais recentes" — mas se chegaram 3 mensagens novas
/// entre uma requisição e outra, essas 3 empurram tudo para baixo e o cliente recebe
/// mensagens repetidas (ou pula algumas). O cursor aponta para um item concreto, então
/// mensagens novas no topo não deslocam nada.
/// </remarks>
public sealed record MessagePage(
    IReadOnlyList<MessageResponse> Items,
    Guid? NextCursor);
