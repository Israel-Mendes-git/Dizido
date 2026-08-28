using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;

namespace Dizido.Contracts.Realtime;

/// <summary>
/// Tudo que o servidor sabe enviar para um cliente conectado.
/// </summary>
/// <remarks>
/// <para>
/// Esta interface fica em <c>Contracts</c> porque os dois lados dependem dela: o servidor a
/// implementa via <c>Hub&lt;IChatClient&gt;</c> e o cliente registra os handlers pelos mesmos
/// nomes de método.
/// </para>
/// <para>
/// O ganho sobre <c>Clients.All.SendAsync("MessageReceived", ...)</c> com string solta é o
/// compilador: errar o nome do evento ou o tipo do argumento vira erro de build, não uma
/// mensagem que some silenciosamente em produção.
/// </para>
/// </remarks>
public interface IChatClient
{
    Task MessageReceived(MessageResponse message);

    Task MessageDeleted(MessageDeletedEvent evt);

    Task TypingChanged(TypingEvent evt);

    Task PresenceChanged(PresenceEvent evt);

    Task ReadReceiptUpdated(ReadReceiptEvent evt);

    /// <summary>Você foi adicionado a uma conversa (ou uma nova foi criada com você).</summary>
    Task ConversationAdded(ConversationResponse conversation);
}
