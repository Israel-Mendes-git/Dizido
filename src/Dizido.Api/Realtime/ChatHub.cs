using Dizido.Api.Auth;
using Dizido.Api.Observabilidade;
using Dizido.Contracts.Realtime;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Realtime;

/// <summary>
/// Conexão persistente por onde o servidor empurra eventos para os clientes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decisão importante:</b> enviar mensagem <b>não</b> é um método deste hub — é um HTTP POST.
/// O hub só notifica. Razões: o POST tem código de status, retry natural e a deduplicação por
/// <c>ClientMessageId</c>; e continua funcionando quando o WebSocket cai, que é justamente
/// quando mais importa a mensagem não se perder. Escrever por um caminho e ser notificado por
/// outro é o padrão da maioria dos apps de mensageria.
/// </para>
/// <para>
/// Cada conversa é um <b>grupo do SignalR</b>. Emitir para o grupo entrega a todas as conexões
/// inscritas nele, sem o servidor precisar saber quem está online.
/// </para>
/// </remarks>
[Authorize]
public sealed partial class ChatHub(
    ICurrentUser currentUser,
    DizidoDbContext db,
    IPresenceTracker presence,
    DizidoMetrics metrics,
    ILogger<ChatHub> logger) : Hub<IChatClient>
{
    public static string GroupName(Guid conversationId) => $"conversation:{conversationId}";

    public override async Task OnConnectedAsync()
    {
        if (currentUser.UserId is not { } me)
        {
            // Não deveria acontecer: [Authorize] já barra anônimos. Mas se acontecer,
            // abortar é melhor do que seguir com uma conexão sem dono.
            Context.Abort();
            return;
        }

        // Inscreve a conexão nos grupos de todas as conversas de que o usuário participa.
        // É isto que faz uma mensagem nova chegar sem o cliente pedir nada.
        var conversationIds = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == me && m.LeftAt == null)
            .Select(m => m.ConversationId)
            .ToListAsync(Context.ConnectionAborted);

        foreach (var id in conversationIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(id));
        }

        var cameOnline = await presence.ConnectedAsync(me, Context.ConnectionId);

        if (cameOnline)
        {
            // Só avisa quem tem alguma conversa com essa pessoa. Transmitir presença para
            // todo mundo seria vazamento de informação e desperdício de banda.
            await BroadcastPresenceAsync(me, isOnline: true, conversationIds);
        }

        metrics.ConexaoAberta();

        LogConnected(logger, me, Context.ConnectionId, conversationIds.Count);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (currentUser.UserId is { } me)
        {
            var wentOffline = await presence.DisconnectedAsync(me, Context.ConnectionId);

            if (wentOffline)
            {
                var conversationIds = await db.ConversationMembers
                    .AsNoTracking()
                    .Where(m => m.UserId == me && m.LeftAt == null)
                    .Select(m => m.ConversationId)
                    .ToListAsync();

                await BroadcastPresenceAsync(me, isOnline: false, conversationIds);
            }

            // Dentro do if, e não fora: a conexão sem usuário identificado é abortada no
            // OnConnectedAsync sem nunca ter sido contada. Decrementar aqui deixaria o
            // contador de conexões abertas caminhando para o negativo a cada tentativa
            // rejeitada — e um gráfico negativo é pior do que nenhum gráfico.
            metrics.ConexaoFechada();
        }

        // Não removemos das Groups manualmente: o SignalR já limpa as inscrições de uma
        // conexão que morreu.
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Avisa a conversa que este usuário está digitando (ou parou).</summary>
    /// <remarks>
    /// O cliente deve aplicar <i>debounce</i>: um evento a cada ~3 segundos enquanto digita,
    /// não um por tecla. Sem isso, uma pessoa escrevendo rápido gera dezenas de mensagens por
    /// segundo para cada membro do grupo.
    /// </remarks>
    public async Task SetTyping(Guid conversationId, bool isTyping)
    {
        if (currentUser.UserId is not { } me)
        {
            return;
        }

        // Confiar no conversationId que o cliente mandou seria permitir que qualquer um
        // enviasse "fulano está digitando" para qualquer conversa. Verificamos sempre.
        var member = await db.ConversationMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m =>
                m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null);

        if (member is null)
        {
            return;
        }

        var name = await db.Profiles
            .AsNoTracking()
            .Where(p => p.Id == me)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync() ?? "alguém";

        // OthersInGroup, e não Group: quem digita não precisa ser avisado de que está digitando.
        await Clients.OthersInGroup(GroupName(conversationId))
            .TypingChanged(new TypingEvent(conversationId, me, name, isTyping));
    }

    /// <summary>Marca a conversa como lida até a mensagem informada.</summary>
    public async Task MarkRead(Guid conversationId, Guid lastReadMessageId)
    {
        if (currentUser.UserId is not { } me)
        {
            return;
        }

        var member = await db.ConversationMembers.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null);

        if (member is null)
        {
            return;
        }

        // A entidade recusa retroceder a marca d'água — recibos podem chegar fora de ordem.
        member.MarkReadUpTo(lastReadMessageId);
        await db.SaveChangesAsync();

        await Clients.Group(GroupName(conversationId))
            .ReadReceiptUpdated(new ReadReceiptEvent(conversationId, me, lastReadMessageId));
    }

    /// <summary>Renova o TTL da presença. O cliente chama a cada ~60 segundos.</summary>
    public async Task Heartbeat()
    {
        if (currentUser.UserId is { } me && presence is RedisPresenceTracker tracker)
        {
            await tracker.RenewAsync(me);
        }
    }

    /// <summary>
    /// Log gerado em tempo de compilação pelo source generator do .NET.
    /// </summary>
    /// <remarks>
    /// Diferença para <c>logger.LogInformation("...{UserId}...", me, ...)</c>: aquela sobrecarga
    /// recebe <c>params object?[]</c>, então cada Guid e cada int é encaixotado (boxing) e um
    /// array é alocado — <b>mesmo quando o nível Information está desligado</b> e a linha vai ser
    /// descartada. Num hub que registra toda conexão, isso é lixo constante para o coletor.
    /// O gerador produz um delegate tipado que só formata se o nível estiver ativo.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Usuário {UserId} conectou ({ConnectionId}) em {Count} conversas")]
    private static partial void LogConnected(ILogger logger, Guid userId, string connectionId, int count);

    private async Task BroadcastPresenceAsync(Guid userId, bool isOnline, IReadOnlyList<Guid> conversationIds)
    {
        var evt = new PresenceEvent(userId, isOnline, DateTimeOffset.UtcNow);

        foreach (var id in conversationIds)
        {
            await Clients.OthersInGroup(GroupName(id)).PresenceChanged(evt);
        }
    }
}
