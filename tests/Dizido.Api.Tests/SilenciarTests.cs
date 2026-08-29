using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Conversations;

namespace Dizido.Api.Tests;

/// <summary>
/// Silenciar é preferência pessoal: afeta só quem pediu, e não avisa ninguém.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class SilenciarTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task SilenciarApareceNaMinhaLinhaEEmMaisNenhuma()
    {
        var (grupo, dono, membro, idDoDono) = await GrupoComMembroAsync();

        var ate = DateTimeOffset.UtcNow.AddHours(8);

        (await dono.PatchAsJsonAsync($"/api/conversations/{grupo}/mute", new MuteRequest(ate)))
            .EnsureSuccessStatusCode();

        var conversa = await VerConversaAsync(dono, grupo);

        var linhaDoDono = conversa.Members.First(m => m.UserId == idDoDono);
        var linhaDoMembro = conversa.Members.First(m => m.UserId != idDoDono);

        Assert.NotNull(linhaDoDono.MutedUntil);

        // Silenciar não contamina os outros participantes.
        Assert.Null(linhaDoMembro.MutedUntil);

        // E o membro, olhando a mesma conversa, também continua sem silêncio na própria linha.
        var comoOMembroVe = await VerConversaAsync(membro, grupo);
        Assert.Null(comoOMembroVe.Members.First(m => m.UserId != idDoDono).MutedUntil);
    }

    [Fact]
    public async Task ReativarLimpaOSilencio()
    {
        var (grupo, dono, _, idDoDono) = await GrupoComMembroAsync();

        (await dono.PatchAsJsonAsync(
            $"/api/conversations/{grupo}/mute",
            new MuteRequest(DateTimeOffset.UtcNow.AddDays(7)))).EnsureSuccessStatusCode();

        Assert.NotNull((await VerConversaAsync(dono, grupo)).Members.First(m => m.UserId == idDoDono).MutedUntil);

        // Nulo é o jeito de dizer "quero ser avisado de novo".
        (await dono.PatchAsJsonAsync($"/api/conversations/{grupo}/mute", new MuteRequest(null)))
            .EnsureSuccessStatusCode();

        Assert.Null((await VerConversaAsync(dono, grupo)).Members.First(m => m.UserId == idDoDono).MutedUntil);
    }

    [Fact]
    public async Task NaoMembroNaoSilenciaConversaAlheia()
    {
        var (grupo, _, _, _) = await GrupoComMembroAsync();
        var estranho = Api.ClienteDe(await Api.CriarUsuarioAsync("estranha"));

        var resposta = await estranho.PatchAsJsonAsync(
            $"/api/conversations/{grupo}/mute", new MuteRequest(DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    /// <summary>
    /// Silenciar não gera aviso de sistema. Ao contrário de entrar, sair ou mudar o título,
    /// isto não é assunto do grupo — e um "Fulano silenciou a conversa" no fluxo seria
    /// constrangedor além de inútil.
    /// </summary>
    [Fact]
    public async Task SilenciarNaoGeraAvisoNoFluxo()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();

        var antes = await ContarMensagensAsync(dono, grupo);

        (await dono.PatchAsJsonAsync(
            $"/api/conversations/{grupo}/mute",
            new MuteRequest(DateTimeOffset.UtcNow.AddHours(1)))).EnsureSuccessStatusCode();

        Assert.Equal(antes, await ContarMensagensAsync(dono, grupo));
    }

    // ----- auxiliares -----

    private static async Task<ConversationResponse> VerConversaAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await VerAsync(cliente, grupo);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    private static async Task<int> ContarMensagensAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await LerMensagensAsync(cliente, grupo);

        resposta.EnsureSuccessStatusCode();

        var pagina = await resposta.Content.ReadFromJsonAsync<Dizido.Contracts.Messages.MessagePage>();

        return pagina!.Items.Count;
    }

    private async Task<(Guid Grupo, HttpClient Dono, HttpClient Membro, Guid IdDoDono)> GrupoComMembroAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        return (grupo.Id, clienteDoDono, Api.ClienteDe(membro), dono.Id);
    }
}
