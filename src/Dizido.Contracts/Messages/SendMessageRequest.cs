namespace Dizido.Contracts.Messages;

/// <summary>Corpo do POST que envia uma mensagem.</summary>
/// <param name="ClientMessageId">
/// Gerado pelo cliente ANTES de enviar. Se a resposta se perder e o cliente reenviar,
/// o servidor reconhece o mesmo identificador e devolve a mensagem já criada em vez de
/// duplicá-la. Sem isso, rede instável produz mensagem repetida.
/// </param>
/// <param name="Body">Texto da mensagem.</param>
/// <param name="ReplyToMessageId">Mensagem sendo respondida, se for uma resposta.</param>
public sealed record SendMessageRequest(
    Guid ClientMessageId,
    string Body,
    Guid? ReplyToMessageId = null);
