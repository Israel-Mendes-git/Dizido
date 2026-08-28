using Dizido.Contracts.Attachments;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Realtime;
using Dizido.Contracts.Sync;

namespace Dizido.Client;

/// <summary>Estado de envio de uma mensagem, do ponto de vista do cliente.</summary>
public enum EstadoEnvio
{
    /// <summary>Na fila. Aparece na tela, mas o servidor ainda não confirmou.</summary>
    Enviando,

    /// <summary>O servidor gravou.</summary>
    Enviada,

    /// <summary>Todos os outros participantes já leram.</summary>
    Lida,

    /// <summary>Falhou depois de várias tentativas. O usuário pode reenviar.</summary>
    Falhou,
}

/// <summary>Uma mensagem na tela, com o estado de envio junto.</summary>
public sealed record MensagemNaTela(MessageResponse Dados, EstadoEnvio Estado)
{
    public bool EhProvisoria => Estado == EstadoEnvio.Enviando;
}

/// <summary>
/// Estado observável do chat no cliente: conversas, mensagens carregadas, quem está digitando,
/// quem está online, e a fila de mensagens pendentes.
/// </summary>
public sealed class ChatStore(
    DizidoApiClient api,
    ChatConnection connection,
    DizidoSession session,
    Outbox outbox,
    TimeProvider clock) : IDisposable
{
    private const int MaxTentativas = 8;

    private readonly Dictionary<Guid, List<MensagemNaTela>> _mensagens = [];
    private readonly Dictionary<Guid, Guid?> _cursorAntigo = [];
    private readonly HashSet<Guid> _temMaisAntigas = [];
    private readonly Dictionary<Guid, DateTimeOffset> _digitando = [];
    private readonly HashSet<Guid> _online = [];
    private readonly Random _aleatorio = new();

    private CancellationTokenSource? _drenagem;

    public event Action? Changed;

    public List<ConversationResponse> Conversas { get; } = [];

    public Guid? ConversaAtiva { get; private set; }

    public bool CarregandoHistorico { get; private set; }

    public bool CarregandoMaisAntigas { get; private set; }

    public int Pendentes => outbox.Itens.Count;

    public IReadOnlyList<MensagemNaTela> MensagensDe(Guid conversationId) =>
        _mensagens.TryGetValue(conversationId, out var lista) ? lista : [];

    public bool TemMaisAntigas(Guid conversationId) => _temMaisAntigas.Contains(conversationId);

    public bool EstaOnline(Guid userId) => _online.Contains(userId);

    public IEnumerable<Guid> QuemEstaDigitando(Guid conversationId, DateTimeOffset agora) =>
        _digitando
            .Where(p => p.Value > agora)
            .Select(p => p.Key)
            .Where(userId => userId != session.UserId)
            .Where(userId => Conversas.Any(c =>
                c.Id == conversationId && c.Members.Any(m => m.UserId == userId)));

    public void Inscrever()
    {
        connection.MessageReceived += AoReceberMensagem;
        connection.ConversationAdded += AoAdicionarConversa;
        connection.PresenceChanged += AoMudarPresenca;
        connection.TypingChanged += AoMudarDigitacao;
        connection.ReadReceiptUpdated += AoAtualizarRecibo;
        connection.Reconectou += AoReconectar;
    }

    public void Desinscrever()
    {
        connection.MessageReceived -= AoReceberMensagem;
        connection.ConversationAdded -= AoAdicionarConversa;
        connection.PresenceChanged -= AoMudarPresenca;
        connection.TypingChanged -= AoMudarDigitacao;
        connection.ReadReceiptUpdated -= AoAtualizarRecibo;
        connection.Reconectou -= AoReconectar;

        _drenagem?.Cancel();
        _drenagem = null;
    }

    /// <summary>Carrega o estado inicial e retoma o que ficou pendente da sessão anterior.</summary>
    public async Task IniciarAsync(CancellationToken ct = default)
    {
        await CarregarConversasAsync(ct);

        // A fila pode ter sobrevivido ao fechamento do navegador. Repovoa a tela com as
        // mensagens que o usuário escreveu e nunca chegaram a sair.
        await outbox.CarregarAsync();

        foreach (var item in outbox.Itens)
        {
            var lista = Lista(item.ConversationId);

            if (lista.All(m => m.Dados.ClientMessageId != item.ClientMessageId))
            {
                lista.Add(new MensagemNaTela(
                    Provisoria(item.ConversationId, item.Body, item.ClientMessageId, item.CriadaEm),
                    EstadoEnvio.Enviando));
            }
        }

        Changed?.Invoke();
        IniciarDrenagem();
    }

    public async Task CarregarConversasAsync(CancellationToken ct = default)
    {
        var lista = await api.GetConversationsAsync(ct);

        Conversas.Clear();
        Conversas.AddRange(lista);

        SemearPresenca(lista.SelectMany(c => c.Members));
        Changed?.Invoke();
    }

    public async Task AbrirConversaAsync(Guid conversationId, CancellationToken ct = default)
    {
        ConversaAtiva = conversationId;
        Changed?.Invoke();

        if (_mensagens.TryGetValue(conversationId, out var jaCarregada) && jaCarregada.Count > 0)
        {
            await MarcarLidoAsync(conversationId);
            return;
        }

        CarregandoHistorico = true;
        Changed?.Invoke();

        try
        {
            var pagina = await api.GetMessagesAsync(conversationId, ct: ct);

            // A API devolve da mais recente para a mais antiga (é o que a paginação por cursor
            // precisa). Na tela queremos o contrário: as antigas em cima.
            var carregadas = pagina.Items.Reverse()
                .Select(m => new MensagemNaTela(m, EstadoFinal(m, conversationId)))
                .ToList();

            // Mensagens ainda na fila entram no fim, para o usuário não achar que sumiram.
            carregadas.AddRange(outbox.Itens
                .Where(i => i.ConversationId == conversationId)
                .Select(i => new MensagemNaTela(
                    Provisoria(conversationId, i.Body, i.ClientMessageId, i.CriadaEm),
                    EstadoEnvio.Enviando)));

            _mensagens[conversationId] = carregadas;
            _cursorAntigo[conversationId] = pagina.NextCursor;

            if (pagina.NextCursor is not null)
            {
                _temMaisAntigas.Add(conversationId);
            }

            await MarcarLidoAsync(conversationId);
        }
        finally
        {
            CarregandoHistorico = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Carrega a página anterior do histórico (mensagens mais antigas).</summary>
    public async Task CarregarMaisAntigasAsync(Guid conversationId, CancellationToken ct = default)
    {
        if (CarregandoMaisAntigas
            || !_cursorAntigo.TryGetValue(conversationId, out var cursor)
            || cursor is null)
        {
            return;
        }

        CarregandoMaisAntigas = true;
        Changed?.Invoke();

        try
        {
            var pagina = await api.GetMessagesAsync(conversationId, cursor, ct: ct);

            // Insere no começo, mantendo a ordem cronológica.
            Lista(conversationId).InsertRange(0, pagina.Items.Reverse()
                .Select(m => new MensagemNaTela(m, EstadoFinal(m, conversationId))));

            _cursorAntigo[conversationId] = pagina.NextCursor;

            if (pagina.NextCursor is null)
            {
                _temMaisAntigas.Remove(conversationId);
            }
        }
        finally
        {
            CarregandoMaisAntigas = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Coloca a mensagem na fila e mostra na tela imediatamente.
    /// </summary>
    /// <remarks>
    /// O envio em si é feito pela drenagem da fila, em segundo plano. Assim escrever uma
    /// mensagem nunca fica esperando a rede: a operação é gravar na fila, que é local e
    /// instantânea, e o resto acontece sozinho — inclusive na próxima abertura do app, se
    /// for preciso.
    /// </remarks>
    public async Task EnviarAsync(
        Guid conversationId,
        string texto,
        AttachmentResponse? anexo = null,
        CancellationToken ct = default)
    {
        var clientMessageId = Guid.NewGuid();
        var agora = clock.GetUtcNow();

        Lista(conversationId).Add(new MensagemNaTela(
            Provisoria(conversationId, texto, clientMessageId, agora, anexo),
            EstadoEnvio.Enviando));

        await outbox.EnfileirarAsync(
            new ItemDaFila(conversationId, clientMessageId, texto, agora, anexo?.Id));

        Changed?.Invoke();
        IniciarDrenagem();
    }

    /// <summary>
    /// Sobe um arquivo e manda a mensagem que o carrega.
    /// </summary>
    /// <remarks>
    /// O upload acontece agora, e não pela fila: sem rede, não há como enviar o arquivo, e
    /// a função devolve o erro para a tela mostrar. Só depois de o anexo estar confirmado a
    /// mensagem entra na fila — daí em diante ela é tão resiliente quanto qualquer outra.
    /// </remarks>
    /// <returns>Mensagem de erro, ou <c>null</c> se deu certo.</returns>
    public async Task<string?> EnviarArquivoAsync(
        Guid conversationId,
        string texto,
        string fileName,
        string contentType,
        Stream conteudo,
        long tamanho,
        CancellationToken ct = default)
    {
        var (anexo, erro) = await api.UploadAsync(
            conversationId, fileName, contentType, conteudo, tamanho, ct);

        if (anexo is null)
        {
            return erro ?? "Não foi possível enviar o arquivo.";
        }

        await EnviarAsync(conversationId, texto, anexo, ct);

        return null;
    }

    /// <summary>Reenfileira uma mensagem que falhou.</summary>
    public async Task ReenviarAsync(MensagemNaTela mensagem)
    {
        ArgumentNullException.ThrowIfNull(mensagem);

        var lista = Lista(mensagem.Dados.ConversationId);
        var indice = lista.FindIndex(m => m.Dados.ClientMessageId == mensagem.Dados.ClientMessageId);

        if (indice >= 0)
        {
            lista[indice] = mensagem with { Estado = EstadoEnvio.Enviando };
        }

        // Volta para a fila com o contador zerado — o usuário pediu explicitamente.
        await outbox.RemoverAsync(mensagem.Dados.ClientMessageId);
        await outbox.EnfileirarAsync(new ItemDaFila(
            mensagem.Dados.ConversationId,
            mensagem.Dados.ClientMessageId,
            mensagem.Dados.Body,
            mensagem.Dados.SentAt,
            mensagem.Dados.Attachment?.Id));

        Changed?.Invoke();
        IniciarDrenagem();
    }

    /// <summary>
    /// Troca um anexo por uma versão com URLs recém-assinadas, onde quer que ele apareça.
    /// </summary>
    /// <remarks>
    /// O mesmo arquivo pode estar em mais de uma mensagem (alguém reenviou a mesma foto), e
    /// por isso a substituição varre todas as conversas carregadas em vez de mexer só na
    /// mensagem que reclamou.
    /// </remarks>
    public void AtualizarAnexo(AttachmentResponse anexo)
    {
        ArgumentNullException.ThrowIfNull(anexo);

        foreach (var lista in _mensagens.Values)
        {
            for (var i = 0; i < lista.Count; i++)
            {
                if (lista[i].Dados.Attachment?.Id == anexo.Id)
                {
                    lista[i] = lista[i] with { Dados = lista[i].Dados with { Attachment = anexo } };
                }
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Fecha a conversa aberta (usado ao sair de um grupo).</summary>
    public Task SairDaConversaAtivaAsync()
    {
        if (ConversaAtiva is { } id)
        {
            _mensagens.Remove(id);
            _cursorAntigo.Remove(id);
            _temMaisAntigas.Remove(id);
            Conversas.RemoveAll(c => c.Id == id);
            ConversaAtiva = null;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    public async Task MarcarLidoAsync(Guid conversationId)
    {
        var ultima = MensagensDe(conversationId).LastOrDefault(m => !m.EhProvisoria);

        if (ultima is not null)
        {
            await connection.MarkReadAsync(conversationId, ultima.Dados.Id);
        }
    }

    // ---------------------------------------------------------------------
    // Fila de saída
    // ---------------------------------------------------------------------

    private void IniciarDrenagem()
    {
        if (_drenagem is not null || !outbox.TemPendencias)
        {
            return;
        }

        _drenagem = new CancellationTokenSource();
        _ = DrenarAsync(_drenagem.Token);
    }

    /// <summary>Envia os itens da fila, um a um, com espera crescente entre falhas.</summary>
    private async Task DrenarAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && outbox.TemPendencias)
            {
                var item = outbox.Itens[0];

                if (item.Tentativas >= MaxTentativas)
                {
                    // Desistimos de tentar sozinhos, mas NÃO descartamos: a mensagem fica
                    // marcada como falha na tela, com botão de reenviar. Apagar em silêncio
                    // o que o usuário escreveu é o pior desfecho possível.
                    MarcarFalha(item);
                    await outbox.RemoverAsync(item.ClientMessageId);
                    Changed?.Invoke();
                    continue;
                }

                var (mensagem, _) = await api.SendMessageAsync(
                    item.ConversationId,
                        new SendMessageRequest(item.ClientMessageId, item.Body, AttachmentId: item.AttachmentId),
                    ct);

                if (mensagem is not null)
                {
                    Confirmar(mensagem);
                    await outbox.RemoverAsync(item.ClientMessageId);
                    Changed?.Invoke();
                    continue;
                }

                await outbox.RegistrarTentativaAsync(item.ClientMessageId);

                var espera = Backoff.Calcular(item.Tentativas + 1, _aleatorio);
                await Task.Delay(espera, clock, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Saída normal quando o usuário desloga ou o componente é descartado.
        }
        finally
        {
            _drenagem = null;
        }
    }

    private void MarcarFalha(ItemDaFila item)
    {
        var lista = Lista(item.ConversationId);
        var indice = lista.FindIndex(m => m.Dados.ClientMessageId == item.ClientMessageId);

        if (indice >= 0)
        {
            lista[indice] = lista[indice] with { Estado = EstadoEnvio.Falhou };
        }
    }

    private void Confirmar(MessageResponse mensagem)
    {
        var lista = Lista(mensagem.ConversationId);
        var indice = lista.FindIndex(m => m.Dados.ClientMessageId == mensagem.ClientMessageId);

        if (indice >= 0)
        {
            lista[indice] = new MensagemNaTela(mensagem, EstadoFinal(mensagem, mensagem.ConversationId));
        }
        else
        {
            lista.Add(new MensagemNaTela(mensagem, EstadoFinal(mensagem, mensagem.ConversationId)));
        }
    }

    // ---------------------------------------------------------------------
    // Sincronização
    // ---------------------------------------------------------------------

    /// <summary>
    /// Busca o que foi perdido enquanto a conexão esteve caída.
    /// </summary>
    /// <remarks>
    /// O SignalR não guarda eventos que não conseguiu entregar. Toda mensagem gravada durante
    /// a queda simplesmente não chegou — e o cliente não tem como saber quantas foram. Por isso
    /// mandamos o último Id conhecido de cada conversa e pedimos o que veio depois.
    /// </remarks>
    private async void AoReconectar()
    {
        try
        {
            var cursores = _mensagens
                .Select(par => new ConversationCursor(
                    par.Key,
                    par.Value.LastOrDefault(m => !m.EhProvisoria)?.Dados.Id))
                .ToList();

            var resposta = await api.SyncAsync(new SyncRequest(cursores));

            Conversas.Clear();
            Conversas.AddRange(resposta.Conversations);
            SemearPresenca(resposta.Conversations.SelectMany(c => c.Members));

            foreach (var mensagem in resposta.Messages)
            {
                Confirmar(mensagem);
            }

            // Uma conversa truncada tem um buraco no meio: descartamos o que temos dela para
            // não montar um histórico com lacuna invisível. A próxima abertura recarrega.
            foreach (var id in resposta.Truncated)
            {
                _mensagens.Remove(id);
                _cursorAntigo.Remove(id);
            }

            Changed?.Invoke();
            IniciarDrenagem();
        }
        catch (Exception)
        {
            // Este método é async void (assinatura de handler de evento): uma exceção que
            // escape daqui derruba o processo. Falhar a sincronização não é fatal — a
            // próxima reconexão tenta de novo.
        }
    }

    // ---------------------------------------------------------------------
    // Eventos vindos do servidor
    // ---------------------------------------------------------------------

    private void AoReceberMensagem(MessageResponse mensagem)
    {
        Confirmar(mensagem);

        var conversa = Conversas.FindIndex(c => c.Id == mensagem.ConversationId);

        if (conversa >= 0)
        {
            var atualizada = Conversas[conversa] with { LastMessageAt = mensagem.SentAt };
            Conversas.RemoveAt(conversa);
            Conversas.Insert(0, atualizada);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Registra uma conversa na lista, substituindo se já existir.
    /// </summary>
    /// <remarks>
    /// Ponto de entrada único, usado tanto por quem criou a conversa quanto pelo evento
    /// ConversationAdded. Ter dois caminhos inserindo direto na lista produzia a MESMA
    /// conversa duas vezes — e o Blazor derruba a renderização inteira quando dois irmãos
    /// compartilham o mesmo @key.
    /// </remarks>
    public void RegistrarConversa(ConversationResponse conversa)
    {
        AoAdicionarConversa(conversa);
    }

    private void AoAdicionarConversa(ConversationResponse conversa)
    {
        var indice = Conversas.FindIndex(c => c.Id == conversa.Id);

        if (indice >= 0)
        {
            Conversas[indice] = conversa;
        }
        else
        {
            Conversas.Insert(0, conversa);
        }

        SemearPresenca(conversa.Members);
        Changed?.Invoke();
    }

    private void AoMudarPresenca(PresenceEvent evt)
    {
        if (evt.IsOnline)
        {
            _online.Add(evt.UserId);
        }
        else
        {
            _online.Remove(evt.UserId);
        }

        Changed?.Invoke();
    }

    private void AoMudarDigitacao(TypingEvent evt)
    {
        if (evt.IsTyping)
        {
            // Guardamos até QUANDO vale, não um booleano. Se o "parou de digitar" se perder
            // (aba fechada, rede caiu), o indicador some sozinho em 5 segundos em vez de
            // ficar para sempre na tela.
            _digitando[evt.UserId] = clock.GetUtcNow().AddSeconds(5);
        }
        else
        {
            _digitando.Remove(evt.UserId);
        }

        Changed?.Invoke();
    }

    private void AoAtualizarRecibo(ReadReceiptEvent evt)
    {
        var indice = Conversas.FindIndex(c => c.Id == evt.ConversationId);

        if (indice < 0)
        {
            return;
        }

        var conversa = Conversas[indice];

        Conversas[indice] = conversa with
        {
            Members = [.. conversa.Members.Select(m => m.UserId == evt.UserId
                ? m with { LastReadMessageId = evt.LastReadMessageId }
                : m)],
        };

        // Reavalia o estado das MINHAS mensagens: algumas podem ter passado de enviadas a lidas.
        if (_mensagens.TryGetValue(evt.ConversationId, out var lista))
        {
            for (var i = 0; i < lista.Count; i++)
            {
                if (lista[i].Estado is EstadoEnvio.Enviada or EstadoEnvio.Lida)
                {
                    lista[i] = lista[i] with { Estado = EstadoFinal(lista[i].Dados, evt.ConversationId) };
                }
            }
        }

        Changed?.Invoke();
    }

    // ---------------------------------------------------------------------
    // Auxiliares
    // ---------------------------------------------------------------------

    /// <summary>
    /// Uma mensagem minha está "lida" quando TODOS os outros membros já a leram.
    /// </summary>
    /// <remarks>
    /// Num grupo, marcar como lida assim que a primeira pessoa leu dá a impressão errada.
    /// Comparamos por Id porque, sendo UUIDv7, "li até o Id X" implica ter lido tudo com Id menor.
    /// </remarks>
    private EstadoEnvio EstadoFinal(MessageResponse mensagem, Guid conversationId)
    {
        if (mensagem.SenderId != session.UserId)
        {
            return EstadoEnvio.Enviada;
        }

        var conversa = Conversas.FirstOrDefault(c => c.Id == conversationId);

        if (conversa is null)
        {
            return EstadoEnvio.Enviada;
        }

        var outros = conversa.Members.Where(m => m.UserId != session.UserId).ToList();

        if (outros.Count == 0)
        {
            return EstadoEnvio.Enviada;
        }

        var todosLeram = outros.All(m =>
            m.LastReadMessageId is { } lido && !EhAnterior(lido, mensagem.Id));

        return todosLeram ? EstadoEnvio.Lida : EstadoEnvio.Enviada;
    }

    /// <summary>
    /// <paramref name="a"/> vem antes de <paramref name="b"/> na ordem temporal dos UUIDv7?
    /// </summary>
    /// <remarks>
    /// Comparamos pelo formato "N" (hexadecimal puro). O CompareTo nativo de Guid no .NET
    /// compara campo a campo, seguindo o layout do struct, e NÃO a ordem dos bytes — então
    /// não reproduz a ordem cronológica do UUIDv7.
    /// </remarks>
    private static bool EhAnterior(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("N"), b.ToString("N")) < 0;

    /// <summary>
    /// A mensagem que aparece na tela antes de o servidor confirmar (interface otimista).
    /// </summary>
    /// <remarks>
    /// Recebe o anexo já pronto, com as URLs assinadas: o upload terminou antes de a mensagem
    /// entrar na fila, então a imagem aparece no balão desde o primeiro instante — e não
    /// depois que o servidor responder.
    /// </remarks>
    private MessageResponse Provisoria(
        Guid conversationId,
        string texto,
        Guid clientMessageId,
        DateTimeOffset quando,
        AttachmentResponse? anexo = null) =>
        new(
            Id: Guid.CreateVersion7(quando),
            ConversationId: conversationId,
            SenderId: session.UserId ?? Guid.Empty,
            SenderDisplayName: session.DisplayName ?? "eu",
            Body: texto,
            ClientMessageId: clientMessageId,
            ReplyToMessageId: null,
            SentAt: quando,
            EditedAt: null,
            IsDeleted: false,
            Attachment: anexo);

    private void SemearPresenca(IEnumerable<ConversationMemberResponse> membros)
    {
        foreach (var membro in membros.Where(m => m.IsOnline))
        {
            _online.Add(membro.UserId);
        }
    }

    /// <summary>Cancela a drenagem em andamento. Chamado quando a aplicação encerra.</summary>
    public void Dispose()
    {
        _drenagem?.Cancel();
        _drenagem?.Dispose();
        _drenagem = null;
    }

    private List<MensagemNaTela> Lista(Guid conversationId)
    {
        if (!_mensagens.TryGetValue(conversationId, out var lista))
        {
            lista = [];
            _mensagens[conversationId] = lista;
        }

        return lista;
    }
}
