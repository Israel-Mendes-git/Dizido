using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dizido.Client;

/// <summary>
/// Conexão em tempo real com o servidor. Encapsula o SignalR para que a UI não precise
/// conhecê-lo.
/// </summary>
public sealed class ChatConnection(DizidoSession session, Uri baseAddress) : IAsyncDisposable
{
    private HubConnection? _hub;
    private Timer? _heartbeat;

    public event Action<MessageResponse>? MessageReceived;

    /// <summary>
    /// Uma mensagem foi apagada por alguém.
    /// </summary>
    /// <remarks>
    /// Evento próprio, e não um <c>MessageReceived</c> com o corpo vazio: quem recebe precisa
    /// saber que aquilo é um apagamento para trocar o balão por "esta mensagem foi apagada".
    /// Um corpo vazio chegando seria indistinguível de uma foto sem legenda.
    /// </remarks>
    public event Action<MessageDeletedEvent>? MessageDeleted;
    public event Action<TypingEvent>? TypingChanged;
    public event Action<PresenceEvent>? PresenceChanged;
    public event Action<ReadReceiptEvent>? ReadReceiptUpdated;
    public event Action<ConversationResponse>? ConversationAdded;
    public event Action<HubConnectionState>? StateChanged;

    /// <summary>Disparado quando a conexão volta depois de uma queda. Gatilho da sincronização.</summary>
    public event Action? Reconectou;

    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_hub is not null)
        {
            return;
        }

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "hubs/chat"), options =>
            {
                // O token é buscado a cada (re)conexão, não capturado uma vez. Numa reconexão
                // após horas offline, o token antigo já teria expirado.
                options.AccessTokenProvider = () => Task.FromResult(session.AccessToken);
            })
            // Reconexão automática com espera crescente. Sem o último valor a lista padrão
            // desiste após ~30s; aqui ela continua tentando de minuto em minuto — um notebook
            // que ficou com a tampa fechada volta sozinho.
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
            ])
            .Build();

        _hub.On<MessageResponse>(nameof(IChatClient.MessageReceived), m => MessageReceived?.Invoke(m));
        _hub.On<MessageDeletedEvent>(nameof(IChatClient.MessageDeleted), e => MessageDeleted?.Invoke(e));
        _hub.On<TypingEvent>(nameof(IChatClient.TypingChanged), e => TypingChanged?.Invoke(e));
        _hub.On<PresenceEvent>(nameof(IChatClient.PresenceChanged), e => PresenceChanged?.Invoke(e));
        _hub.On<ReadReceiptEvent>(nameof(IChatClient.ReadReceiptUpdated), e => ReadReceiptUpdated?.Invoke(e));
        _hub.On<ConversationResponse>(nameof(IChatClient.ConversationAdded), c => ConversationAdded?.Invoke(c));

        _hub.Reconnecting += _ => { StateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _hub.Reconnected += _ =>
        {
            StateChanged?.Invoke(HubConnectionState.Connected);

            // A reconexão restabelece o canal, mas NÃO reentrega o que se perdeu enquanto
            // ele esteve caído. Quem preenche o buraco é a sincronização.
            Reconectou?.Invoke();

            return Task.CompletedTask;
        };
        _hub.Closed += _ => { StateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };

        await _hub.StartAsync(ct);
        StateChanged?.Invoke(HubConnectionState.Connected);

        // Renova o TTL da presença no Redis. Se o processo morrer sem avisar, o TTL expira
        // e o usuário some da lista de online sozinho.
        _heartbeat = new Timer(async void (_) =>
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync("Heartbeat");
                }
            }
            catch (Exception)
            {
                // Heartbeat perdido não é problema: o próximo renova, e o TTL de 2 minutos
                // dá margem. Nunca deixe uma exceção escapar de um callback de Timer —
                // em async void, ela derruba o processo.
            }
        }, null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45));
    }

    public async Task SetTypingAsync(Guid conversationId, bool isTyping)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.InvokeAsync("SetTyping", conversationId, isTyping);
        }
    }

    public async Task MarkReadAsync(Guid conversationId, Guid lastReadMessageId)
    {
        if (_hub?.State == HubConnectionState.Connected)
        {
            await _hub.InvokeAsync("MarkRead", conversationId, lastReadMessageId);
        }
    }

    public async Task StopAsync()
    {
        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync();
            _heartbeat = null;
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
