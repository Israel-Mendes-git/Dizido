using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Base das classes de teste: guarda a fábrica e concentra as chamadas HTTP.
/// </summary>
/// <remarks>
/// Os atalhos existem para que cada teste caiba em poucas linhas e o que ele verifica fique
/// óbvio. Repare que eles devolvem o <see cref="HttpResponseMessage"/> cru, sem lançar em erro:
/// aqui o código de status <b>é</b> o resultado sob teste.
/// </remarks>
public abstract class TesteDeApi(DizidoApiFactory api)
{
    protected DizidoApiFactory Api { get; } = api;

    protected static async Task<ConversationResponse> CriarGrupoAsync(HttpClient dono, string titulo)
    {
        var resposta = await dono.PostAsJsonAsync("/api/conversations/groups", new CreateGroupRequest(titulo));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    protected static Task<HttpResponseMessage> VerAsync(HttpClient cliente, Guid grupo) =>
        cliente.GetAsync(new Uri($"/api/conversations/{grupo}", UriKind.Relative));

    protected static Task<HttpResponseMessage> RenomearAsync(HttpClient cliente, Guid grupo, string titulo) =>
        cliente.PatchAsJsonAsync($"/api/conversations/{grupo}/title", new RenameGroupRequest(titulo));

    protected static Task<HttpResponseMessage> AdicionarAsync(HttpClient cliente, Guid grupo, Guid usuario) =>
        cliente.PostAsync(new Uri($"/api/conversations/{grupo}/members/{usuario}", UriKind.Relative), null);

    protected static Task<HttpResponseMessage> RemoverAsync(HttpClient cliente, Guid grupo, Guid usuario) =>
        cliente.DeleteAsync(new Uri($"/api/conversations/{grupo}/members/{usuario}", UriKind.Relative));

    protected static Task<HttpResponseMessage> DefinirCargoAsync(
        HttpClient cliente, Guid grupo, Guid usuario, string cargo) =>
        cliente.PatchAsJsonAsync($"/api/conversations/{grupo}/members/{usuario}/role", new ChangeRoleRequest(cargo));

    protected static Task<HttpResponseMessage> TransferirAsync(HttpClient cliente, Guid grupo, Guid usuario) =>
        cliente.PostAsync(new Uri($"/api/conversations/{grupo}/owner/{usuario}", UriKind.Relative), null);

    protected static Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, Guid grupo, string texto) =>
        cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), texto));

    protected static Task<HttpResponseMessage> LerMensagensAsync(HttpClient cliente, Guid grupo) =>
        cliente.GetAsync(new Uri($"/api/conversations/{grupo}/messages", UriKind.Relative));

    /// <summary>
    /// Confere que a resposta é 400 e que o motivo é o esperado.
    /// </summary>
    /// <remarks>
    /// Checar só o código de status não bastaria: um 400 por título inválido, por rota errada
    /// ou por JSON malformado passaria igual, e o teste diria "permissão negada" sem que a
    /// permissão tivesse sido consultada. O texto vem do <c>detail</c> do ProblemDetails, que
    /// é a mensagem da DomainException lançada pela entidade.
    /// </remarks>
    protected static async Task RecusadoPorRegraAsync(HttpResponseMessage resposta, string trechoEsperado)
    {
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaHttp>();

        Assert.NotNull(problema?.Detail);
        Assert.Contains(trechoEsperado, problema.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Só os campos do ProblemDetails que os testes consultam.</summary>
    protected sealed record ProblemaHttp(string? Title, string? Detail, int? Status);
}
