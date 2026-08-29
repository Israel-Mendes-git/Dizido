namespace Dizido.Contracts.Messages;

/// <summary>Corpo do POST que envia uma mensagem.</summary>
/// <param name="ClientMessageId">
/// Gerado pelo cliente ANTES de enviar. Se a resposta se perder e o cliente reenviar,
/// o servidor reconhece o mesmo identificador e devolve a mensagem já criada em vez de
/// duplicá-la. Sem isso, rede instável produz mensagem repetida.
/// </param>
/// <param name="Body">Texto da mensagem.</param>
/// <param name="ReplyToMessageId">Mensagem sendo respondida, se for uma resposta.</param>
/// <param name="AttachmentId">
/// Anexo já enviado e confirmado. Com ele, o <paramref name="Body"/> vira legenda e pode ficar
/// vazio — mandar foto sem escrever nada é o caso mais comum.
/// </param>
public sealed record SendMessageRequest(
    Guid ClientMessageId,
    string Body,
    Guid? ReplyToMessageId = null,
    Guid? AttachmentId = null,

    /// <summary>Quem foi citado com <c>@</c>.</summary>
    /// <remarks>
    /// O cliente manda os identificadores, resolvidos no autocompletar — não o texto. O
    /// servidor confere que cada um participa da conversa antes de aceitar.
    /// </remarks>
    IReadOnlyList<Guid>? MentionedUserIds = null);

/// <summary>Corpo do PATCH que edita uma mensagem já enviada.</summary>
/// <remarks>
/// Só o texto muda. Trocar o anexo de uma mensagem seria outra mensagem — e o histórico ficaria
/// mentindo para quem já tinha visto a original.
/// </remarks>
public sealed record EditMessageRequest(string Body);
