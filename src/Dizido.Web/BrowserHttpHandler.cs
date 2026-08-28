using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Dizido.Web;

/// <summary>
/// Faz o navegador enviar cookies nas chamadas à API.
/// </summary>
/// <remarks>
/// <para>
/// Por padrão, o <c>fetch</c> do navegador <b>não envia cookies</b> para outra origem — e em
/// desenvolvimento o Blazor roda numa porta e a API em outra. Sem isto, o cookie
/// <c>dizido_refresh</c> nunca chegaria ao <c>/auth/refresh</c>, e recarregar a página exigiria
/// login toda vez.
/// </para>
/// <para>
/// <c>BrowserRequestCredentials.Include</c> é o equivalente a <c>credentials: 'include'</c> do
/// fetch. Do lado do servidor, isso exige CORS com <c>AllowCredentials()</c> e uma origem
/// explícita — o navegador recusa <c>*</c> junto com credenciais, de propósito.
/// </para>
/// </remarks>
internal sealed class BrowserHttpHandler : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        return base.SendAsync(request, cancellationToken);
    }
}
