using Dizido.Contracts.Conversations;
using Dizido.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Dizido.Api.Realtime;

public interface IConversationNotifier
{
    /// <summary>
    /// Inscreve as conexões abertas dos membros no grupo da conversa e os avisa dela.
    /// </summary>
    Task ConversationCreatedAsync(ConversationResponse conversation, IReadOnlyList<Guid> memberIds);

    /// <summary>Inscreve as conexões de um novo membro no grupo de uma conversa existente.</summary>
    Task MemberAddedAsync(ConversationResponse conversation, Guid userId);
}

/// <summary>
/// Resolve um problema específico de tempo real: uma conexão só recebe eventos dos grupos em
/// que estava inscrita quando conectou.
/// </summary>
/// <remarks>
/// Cenário concreto: a Ana está com o app aberto. O Bruno abre uma conversa direta com ela.
/// A conexão da Ana foi inscrita nos grupos dela lá no <c>OnConnectedAsync</c> — e essa conversa
/// não existia. Sem inscrevê-la agora, a Ana não veria a mensagem chegar; teria que recarregar
/// a página, o que num app de mensagens é um defeito óbvio.
/// <para>
/// Os connectionIds vêm do Redis — o mesmo lugar onde a presença é mantida. É por isso que o
/// tracker guarda um conjunto de conexões por usuário, e não apenas um sinal de online/offline.
/// </para>
/// </remarks>
public sealed class ConversationNotifier(
    IHubContext<ChatHub, IChatClient> hub,
    IPresenceTracker presence) : IConversationNotifier
{
    public async Task ConversationCreatedAsync(
        ConversationResponse conversation,
        IReadOnlyList<Guid> memberIds)
    {
        foreach (var userId in memberIds)
        {
            await SubscribeAsync(conversation.Id, userId);
        }

        await NotificarAsync(conversation, memberIds);
    }

    public async Task MemberAddedAsync(ConversationResponse conversation, Guid userId)
    {
        await SubscribeAsync(conversation.Id, userId);

        // Avisa o grupo (quem já estava dentro) E o novo membro individualmente.
        await NotificarAsync(conversation, [userId]);
    }

    /// <summary>
    /// Entrega o evento ao grupo e, separadamente, a cada usuário afetado.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A entrega direta ao usuário não é redundância: <c>AddToGroupAsync</c> <b>não é síncrono
    /// quando há backplane Redis</b>. Ele publica a inscrição no Redis e ela é aplicada logo
    /// depois — então emitir para o grupo na linha seguinte pode alcançar um grupo em que a
    /// conexão ainda não entrou, e o evento se perde.
    /// </para>
    /// <para>
    /// O sintoma disso é cruel: funciona quase sempre, e falha de vez em quando, sem erro
    /// nenhum no log. <c>Clients.User</c> resolve a conexão pelo identificador do usuário
    /// (a claim <c>sub</c> do JWT) e não depende de inscrição em grupo — a entrega é imediata.
    /// </para>
    /// <para>
    /// O evento é idempotente do lado do cliente (<c>RegistrarConversa</c> substitui em vez de
    /// duplicar), então receber pelos dois caminhos é inofensivo.
    /// </para>
    /// </remarks>
    private async Task NotificarAsync(ConversationResponse conversation, IReadOnlyList<Guid> afetados)
    {
        await hub.Clients.Group(ChatHub.GroupName(conversation.Id)).ConversationAdded(conversation);

        foreach (var userId in afetados)
        {
            await hub.Clients.User(userId.ToString()).ConversationAdded(conversation);
        }
    }

    private async Task SubscribeAsync(Guid conversationId, Guid userId)
    {
        // Um usuário offline simplesmente não tem conexões — nada a fazer. Quando ele conectar,
        // o OnConnectedAsync o inscreve, porque aí a conversa já existe no banco.
        var connections = await presence.GetConnectionsAsync(userId);

        foreach (var connectionId in connections)
        {
            await hub.Groups.AddToGroupAsync(connectionId, ChatHub.GroupName(conversationId));
        }
    }
}
