using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dizido.Contracts.Auth;

namespace Dizido.Client;

/// <summary>
/// Injeta o token em toda requisição e renova a sessão quando ele expira.
/// </summary>
/// <remarks>
/// <para>
/// Um <see cref="DelegatingHandler"/> é um middleware do lado do cliente: toda chamada do
/// <c>HttpClient</c> passa por aqui antes de sair. É por isso que nenhum dos métodos do
/// <see cref="DizidoApiClient"/> menciona <c>Authorization</c> — seria repetir a mesma linha
/// em quinze lugares e esquecer em um.
/// </para>
/// <para>
/// Além de injetar, ele trata o 401: renova o token e <b>repete a requisição original</b> uma
/// única vez. Do ponto de vista da tela, a expiração do token simplesmente não existe.
/// </para>
/// </remarks>
public sealed class AuthorizationHandler(DizidoSession session, TimeProvider clock) : DelegatingHandler
{
    // Se três chamadas tomarem 401 ao mesmo tempo, sem isto as três disparariam renovações
    // simultâneas. Com rotação de refresh token, a primeira invalidaria o token das outras
    // duas — que seriam interpretadas como REUSO e derrubariam a sessão inteira.
    // Este semáforo garante uma renovação por vez.
    private static readonly SemaphoreSlim RenovacaoLock = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A renovação em si não pode carregar o token velho nem entrar em recursão.
        var ehRenovacao = request.RequestUri?.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal) == true;

        if (!ehRenovacao && session.NeedsRefresh(clock.GetUtcNow()))
        {
            // Renovação preventiva: melhor renovar 2 minutos antes do que descobrir que
            // expirou no meio do envio de uma mensagem.
            await RenovarAsync(cancellationToken);
        }

        Autenticar(request);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized || ehRenovacao)
        {
            return response;
        }

        // Tomou 401 mesmo assim (relógio fora de sincronia, token revogado no servidor...).
        // Uma tentativa de renovar e repetir.
        if (!await RenovarAsync(cancellationToken))
        {
            session.Clear();
            return response;
        }

        response.Dispose();

        var repetida = await ClonarAsync(request, cancellationToken);
        Autenticar(repetida);

        return await base.SendAsync(repetida, cancellationToken);
    }

    private void Autenticar(HttpRequestMessage request)
    {
        if (session.AccessToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private async Task<bool> RenovarAsync(CancellationToken ct)
    {
        await RenovacaoLock.WaitAsync(ct);

        try
        {
            // Outra chamada pode ter renovado enquanto esperávamos o semáforo.
            if (!session.NeedsRefresh(clock.GetUtcNow()) && session.IsAuthenticated)
            {
                return true;
            }

            using var pedido = new HttpRequestMessage(HttpMethod.Post, "api/auth/refresh");
            using var resposta = await base.SendAsync(pedido, ct);

            if (!resposta.IsSuccessStatusCode)
            {
                return false;
            }

            var auth = await resposta.Content.ReadFromJsonAsync<AuthResponse>(ct);

            if (auth is null)
            {
                return false;
            }

            session.Set(auth);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        finally
        {
            RenovacaoLock.Release();
        }
    }

    /// <summary>
    /// Uma <see cref="HttpRequestMessage"/> não pode ser enviada duas vezes — o conteúdo já
    /// foi consumido. Para repetir, é preciso clonar.
    /// </summary>
    private static async Task<HttpRequestMessage> ClonarAsync(HttpRequestMessage original, CancellationToken ct)
    {
        var copia = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
        };

        foreach (var header in original.Headers)
        {
            copia.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(ct);
            copia.Content = new ByteArrayContent(bytes);

            foreach (var header in original.Content.Headers)
            {
                copia.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copia;
    }
}
