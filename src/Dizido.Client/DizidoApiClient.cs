using Dizido.Contracts.Attachments;
using Dizido.Contracts.Auth;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Sync;
using Dizido.Contracts.Users;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Dizido.Client;

/// <summary>
/// Cliente tipado da API do Dizido. É o único lugar do cliente que sabe as URLs.
/// </summary>
/// <remarks>
/// Este projeto não referencia nada de UI. Um app desktop, um bot ou um teste podem usá-lo
/// igual — é o "SDK" do Dizido, e é o que torna a Fase 10 (desktop) barata.
/// </remarks>
public sealed class DizidoApiClient(HttpClient http, DizidoSession session)
{
    public HttpClient Http => http;

    // ----- autenticação -----

    public async Task<(AuthResponse? Auth, string? Erro)> RegisterAsync(
        string email, string password, string displayName, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/auth/register",
            new RegisterRequest(email, password, displayName), ct);

        return await LerAutenticacaoAsync(res, ct);
    }

    public async Task<(AuthResponse? Auth, string? Erro)> LoginAsync(
        string email, string password, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/auth/login", new LoginRequest(email, password), ct);

        return await LerAutenticacaoAsync(res, ct);
    }

    /// <summary>
    /// Tenta restaurar a sessão usando o cookie de refresh. Chamado na inicialização.
    /// </summary>
    /// <returns>true se havia sessão válida.</returns>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await http.PostAsync("api/auth/refresh", content: null, ct);

            if (!res.IsSuccessStatusCode)
            {
                return false;
            }

            var auth = await res.Content.ReadFromJsonAsync<AuthResponse>(ct);

            if (auth is null)
            {
                return false;
            }

            session.Set(auth);
            return true;
        }
        catch (HttpRequestException)
        {
            // API fora do ar na inicialização não deve quebrar a aplicação inteira —
            // a tela de login aparece e o usuário tenta de novo.
            return false;
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await http.PostAsync("api/auth/logout", content: null, ct);
        }
        finally
        {
            // Limpa o estado local mesmo se a chamada falhar: do ponto de vista do usuário,
            // clicar em "sair" tem que sair.
            session.Clear();
        }
    }

    // ----- usuários -----

    public Task<UserResponse?> GetMeAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<UserResponse>("api/users/me", ct);

    public async Task<IReadOnlyList<UserResponse>> GetUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<UserResponse>>("api/users", ct) ?? [];

    // ----- conversas -----

    public async Task<IReadOnlyList<ConversationResponse>> GetConversationsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ConversationResponse>>("api/conversations", ct) ?? [];

    public async Task<ConversationResponse?> OpenDirectAsync(Guid otherUserId, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/conversations/direct",
            new CreateDirectRequest(otherUserId), ct);

        return res.IsSuccessStatusCode
            ? await res.Content.ReadFromJsonAsync<ConversationResponse>(ct)
            : null;
    }

    public async Task<ConversationResponse?> CreateGroupAsync(string title, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/conversations/groups",
            new CreateGroupRequest(title), ct);

        return res.IsSuccessStatusCode
            ? await res.Content.ReadFromJsonAsync<ConversationResponse>(ct)
            : null;
    }

    // ----- administração de grupo -----
    //
    // Estes métodos devolvem `null` em caso de sucesso e a mensagem de erro caso contrário.
    // A interface só precisa mostrar o que o servidor disse — as regras de quem pode o quê
    // vivem no domínio, e a mensagem já vem pronta e explicativa de lá.

    public Task<string?> AddMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PostAsync($"api/conversations/{conversationId}/members/{userId}", null, ct), ct);

    public Task<string?> RemoveMemberAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        ExecutarAsync(() => http.DeleteAsync($"api/conversations/{conversationId}/members/{userId}", ct), ct);

    public Task<string?> RenameGroupAsync(Guid conversationId, string title, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PatchAsJsonAsync(
            $"api/conversations/{conversationId}/title", new RenameGroupRequest(title), ct), ct);

    public Task<string?> ChangeRoleAsync(Guid conversationId, Guid userId, string role, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PatchAsJsonAsync(
            $"api/conversations/{conversationId}/members/{userId}/role", new ChangeRoleRequest(role), ct), ct);

    public Task<string?> TransferOwnershipAsync(Guid conversationId, Guid userId, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PostAsync($"api/conversations/{conversationId}/owner/{userId}", null, ct), ct);

    public Task<string?> MuteAsync(Guid conversationId, DateTimeOffset? until, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PatchAsJsonAsync(
            $"api/conversations/{conversationId}/mute", new MuteRequest(until), ct), ct);

    private static async Task<string?> ExecutarAsync(
        Func<Task<HttpResponseMessage>> chamada, CancellationToken ct)
    {
        using var res = await chamada();

        return res.IsSuccessStatusCode ? null : await LerProblemaAsync(res, ct);
    }

    // ----- mensagens -----

    public async Task<MessagePage> GetMessagesAsync(
        Guid conversationId, Guid? before = null, int limit = 50, CancellationToken ct = default)
    {
        var url = $"api/conversations/{conversationId}/messages?limit={limit}"
                  + (before is null ? string.Empty : $"&before={before}");

        return await http.GetFromJsonAsync<MessagePage>(url, ct) ?? new MessagePage([], null);
    }

    public async Task<(MessageResponse? Mensagem, string? Erro)> SendMessageAsync(
        Guid conversationId, SendMessageRequest request, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync($"api/conversations/{conversationId}/messages", request, ct);

        if (res.IsSuccessStatusCode)
        {
            return (await res.Content.ReadFromJsonAsync<MessageResponse>(ct), null);
        }

        return (null, await LerProblemaAsync(res, ct));
    }

    /// <summary>Edita o texto de uma mensagem já enviada.</summary>
    public Task<string?> EditMessageAsync(
        Guid conversationId, Guid messageId, string body, CancellationToken ct = default) =>
        ExecutarAsync(() => http.PatchAsJsonAsync(
            $"api/conversations/{conversationId}/messages/{messageId}",
            new EditMessageRequest(body), ct), ct);

    /// <summary>Apaga uma mensagem. O autor sempre pode; administradores, a de qualquer um.</summary>
    public Task<string?> DeleteMessageAsync(
        Guid conversationId, Guid messageId, CancellationToken ct = default) =>
        ExecutarAsync(() => http.DeleteAsync(
            $"api/conversations/{conversationId}/messages/{messageId}", ct), ct);

    // ----- anexos -----

    /// <summary>
    /// Sobe um arquivo e devolve o anexo pronto para virar mensagem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Os três passos ficam escondidos aqui dentro de propósito: quem chama quer "sobe este
    /// arquivo", não coreografar pedido, PUT e confirmação. Note que o passo do meio usa um
    /// <see cref="HttpClient"/> <b>novo</b>, e não o da API — o do Dizido carrega o token de
    /// acesso em toda requisição, e mandá-lo para o storage entregaria a sessão do usuário a
    /// um serviço que não tem nada que ver com ela.
    /// </para>
    /// <para>
    /// O relatório de progresso não existe aqui: o navegador não expõe progresso de upload
    /// pelo <c>HttpClient</c> do .NET. Para uma barra de verdade seria preciso passar pelo
    /// JavaScript, e isso fica para quando incomodar.
    /// </para>
    /// </remarks>
    public async Task<(AttachmentResponse? Anexo, string? Erro)> UploadAsync(
        Guid conversationId,
        string fileName,
        string contentType,
        Stream conteudo,
        long tamanho,
        CancellationToken ct = default)
    {
        var pedido = await http.PostAsJsonAsync(
            $"api/conversations/{conversationId}/attachments",
            new RequestUploadRequest(fileName, contentType, tamanho),
            ct);

        if (!pedido.IsSuccessStatusCode)
        {
            return (null, await LerProblemaAsync(pedido, ct));
        }

        var bilhete = await pedido.Content.ReadFromJsonAsync<UploadTicketResponse>(ct);

        if (bilhete is null)
        {
            return (null, "O servidor não devolveu a autorização de upload.");
        }

        // O stream que o navegador entrega ao ler um arquivo escolhido não sabe o próprio
        // tamanho. Um StreamContent sobre ele viraria "Transfer-Encoding: chunked", e o S3
        // recusa isso numa URL assinada — a assinatura pressupõe um corpo de tamanho conhecido.
        // Copiar para memória resolve; o teto de 50 MB é o que torna isso aceitável.
        await using var emMemoria = conteudo.CanSeek ? null : new MemoryStream();

        if (emMemoria is not null)
        {
            await conteudo.CopyToAsync(emMemoria, ct);
            emMemoria.Position = 0;
        }

        using (var direto = new HttpClient())
        using (var corpo = new StreamContent(emMemoria ?? conteudo))
        {
            // O Content-Type faz parte da assinatura. Mandar outro faz o storage recusar
            // com 403, e a mensagem dele não explica nada.
            corpo.Headers.ContentType = new MediaTypeHeaderValue(bilhete.ContentType);

            var envio = await direto.PutAsync(new Uri(bilhete.UploadUrl), corpo, ct);

            if (!envio.IsSuccessStatusCode)
            {
                return (null, $"O envio do arquivo falhou ({(int)envio.StatusCode}).");
            }
        }

        var confirmacao = await http.PostAsync($"api/attachments/{bilhete.AttachmentId}/complete", null, ct);

        return confirmacao.IsSuccessStatusCode
            ? (await confirmacao.Content.ReadFromJsonAsync<AttachmentResponse>(ct), null)
            : (null, await LerProblemaAsync(confirmacao, ct));
    }

    /// <summary>Renova as URLs de um anexo cujas assinaturas expiraram.</summary>
    public async Task<AttachmentResponse?> RefreshAttachmentAsync(Guid id, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"api/attachments/{id}", ct);

        return res.IsSuccessStatusCode
            ? await res.Content.ReadFromJsonAsync<AttachmentResponse>(ct)
            : null;
    }

    // ----- busca -----

    /// <summary>
    /// Procura no histórico das conversas de que o usuário participa.
    /// </summary>
    /// <param name="conversationId">Limita a uma conversa. Nulo procura em todas.</param>
    /// <remarks>
    /// O escopo é decidido no servidor, a partir de quem está autenticado. O cliente não
    /// consegue ampliar a busca para conversas alheias mudando parâmetro nenhum.
    /// </remarks>
    public async Task<IReadOnlyList<MessageResponse>> SearchAsync(
        string termo, Guid? conversationId = null, CancellationToken ct = default)
    {
        var url = $"api/search?q={Uri.EscapeDataString(termo)}"
                  + (conversationId is null ? string.Empty : $"&conversationId={conversationId}");

        var pagina = await http.GetFromJsonAsync<MessagePage>(url, ct);

        return pagina?.Items ?? [];
    }

    // ----- sincronização -----

    /// <summary>Busca tudo que aconteceu depois dos cursores informados.</summary>
    public async Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct = default)
    {
        var res = await http.PostAsJsonAsync("api/sync", request, ct);

        res.EnsureSuccessStatusCode();

        return await res.Content.ReadFromJsonAsync<SyncResponse>(ct)
               ?? new SyncResponse([], [], []);
    }

    // ----- auxiliares -----

    private async Task<(AuthResponse?, string?)> LerAutenticacaoAsync(
        HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            return (null, await LerProblemaAsync(res, ct));
        }

        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>(ct);

        if (auth is not null)
        {
            session.Set(auth);
        }

        return (auth, null);
    }

    /// <summary>Extrai uma mensagem legível de uma resposta ProblemDetails.</summary>
    private static async Task<string> LerProblemaAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Muitas tentativas seguidas. Espere um minuto e tente de novo.";
        }

        try
        {
            var problema = await res.Content.ReadFromJsonAsync<ProblemaDetalhado>(ct);

            if (problema?.Errors is { Count: > 0 })
            {
                return string.Join(" ", problema.Errors.SelectMany(e => e.Value));
            }

            return problema?.Detail ?? problema?.Title ?? $"Erro {(int)res.StatusCode}.";
        }
        catch (Exception e) when (e is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return $"Erro {(int)res.StatusCode}.";
        }
    }

    private sealed record ProblemaDetalhado(
        string? Title,
        string? Detail,
        int? Status,
        Dictionary<string, string[]>? Errors);
}
