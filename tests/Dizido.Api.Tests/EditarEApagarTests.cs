using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Editar e apagar mensagem: quem pode, quem não pode, e o que sobra depois.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class EditarEApagarTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task AutorEditaAPropriaMensagem()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var mensagem = await EnviarEObterAsync(cliente, grupo, "testo");

        var resposta = await EditarAsync(cliente, grupo, mensagem.Id, "texto");

        resposta.EnsureSuccessStatusCode();

        var editada = (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.Equal("texto", editada.Body);
        Assert.NotNull(editada.EditedAt);

        // E a edição persiste: não é só o que a resposta do PATCH devolveu.
        var doHistorico = await BuscarNoHistoricoAsync(cliente, grupo, mensagem.Id);
        Assert.Equal("texto", doHistorico?.Body);
    }

    [Fact]
    public async Task NinguemEditaAMensagemDeOutro()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(dono, grupo, "minha mensagem");

        await RecusadoPorRegraAsync(
            await EditarAsync(membro, grupo, mensagem.Id, "sequestrada"),
            "só o autor");
    }

    /// <summary>
    /// Nem o dono do grupo edita a mensagem de outra pessoa. Moderar é poder apagar, não
    /// poder reescrever o que alguém disse.
    /// </summary>
    [Fact]
    public async Task NemAdministradorEditaMensagemAlheia()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(membro, grupo, "opinião impopular");

        await RecusadoPorRegraAsync(
            await EditarAsync(dono, grupo, mensagem.Id, "opinião conveniente"),
            "só o autor");
    }

    [Fact]
    public async Task NaoMembroNaoEditaNemApaga()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var estranho = Api.ClienteDe(await Api.CriarUsuarioAsync("estranha"));

        var mensagem = await EnviarEObterAsync(cliente, grupo, "assunto interno");

        // 404, e não 400: para quem não participa, a conversa não existe — a permissão nem
        // chega a ser consultada.
        Assert.Equal(HttpStatusCode.NotFound, (await EditarAsync(estranho, grupo, mensagem.Id, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApagarAsync(estranho, grupo, mensagem.Id)).StatusCode);
    }

    [Fact]
    public async Task AutorApagaEOBalaoFicaVazio()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var mensagem = await EnviarEObterAsync(cliente, grupo, "arrependimento");

        (await ApagarAsync(cliente, grupo, mensagem.Id)).EnsureSuccessStatusCode();

        // A mensagem continua no histórico, marcada — apagá-la de verdade quebraria as
        // respostas que apontam para ela e as marcas de leitura dos membros.
        var depois = await BuscarNoHistoricoAsync(cliente, grupo, mensagem.Id);

        Assert.NotNull(depois);
        Assert.True(depois.IsDeleted);
        Assert.Equal(string.Empty, depois.Body);
    }

    [Fact]
    public async Task AdministradorApagaMensagemDeOutro()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(membro, grupo, "algo fora de lugar");

        (await ApagarAsync(dono, grupo, mensagem.Id)).EnsureSuccessStatusCode();

        Assert.True((await BuscarNoHistoricoAsync(dono, grupo, mensagem.Id))?.IsDeleted);
    }

    [Fact]
    public async Task MembroComumNaoApagaMensagemDeOutro()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(dono, grupo, "mensagem do dono");

        await RecusadoPorRegraAsync(
            await ApagarAsync(membro, grupo, mensagem.Id),
            "administrador");
    }

    [Fact]
    public async Task MensagemApagadaNaoPodeSerEditada()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var mensagem = await EnviarEObterAsync(cliente, grupo, "some");

        (await ApagarAsync(cliente, grupo, mensagem.Id)).EnsureSuccessStatusCode();

        await RecusadoPorRegraAsync(
            await EditarAsync(cliente, grupo, mensagem.Id, "voltei"),
            "apagada");
    }

    /// <summary>Apagar duas vezes não é erro — o retry do cliente precisa ser seguro.</summary>
    [Fact]
    public async Task ApagarDuasVezesEhIdempotente()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var mensagem = await EnviarEObterAsync(cliente, grupo, "vai");

        (await ApagarAsync(cliente, grupo, mensagem.Id)).EnsureSuccessStatusCode();
        (await ApagarAsync(cliente, grupo, mensagem.Id)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AvisoDoSistemaNaoPodeSerApagado()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        // Adicionar o membro gerou um aviso de sistema no fluxo.
        var pagina = await LerPaginaAsync(dono, grupo);
        var aviso = pagina.Items.First(m => m.Kind == "System");

        await RecusadoPorRegraAsync(await ApagarAsync(dono, grupo, aviso.Id), "sistema");
    }

    [Fact]
    public async Task MensagemDeOutraConversaNaoEhAlcancavelPelaRota()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var outro = await CriarGrupoAsync(cliente, "Outro grupo");

        var mensagem = await EnviarEObterAsync(cliente, grupo, "aqui");

        // Mesmo sendo dono dos dois grupos e autor da mensagem: o id da conversa na rota não
        // bate com o da mensagem, então ela não existe naquele caminho.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ApagarAsync(cliente, outro.Id, mensagem.Id)).StatusCode);
    }

    // ----- auxiliares -----

    private static Task<HttpResponseMessage> EditarAsync(
        HttpClient cliente, Guid grupo, Guid mensagem, string texto) =>
        cliente.PatchAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem}", new EditMessageRequest(texto));

    private static Task<HttpResponseMessage> ApagarAsync(HttpClient cliente, Guid grupo, Guid mensagem) =>
        cliente.DeleteAsync(new Uri($"/api/conversations/{grupo}/messages/{mensagem}", UriKind.Relative));

    private static async Task<MessageResponse> EnviarEObterAsync(
        HttpClient cliente, Guid grupo, string texto)
    {
        var resposta = await EnviarAsync(cliente, grupo, texto);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;
    }

    private static async Task<MessagePage> LerPaginaAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await LerMensagensAsync(cliente, grupo);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessagePage>())!;
    }

    private static async Task<MessageResponse?> BuscarNoHistoricoAsync(
        HttpClient cliente, Guid grupo, Guid mensagem)
    {
        var pagina = await LerPaginaAsync(cliente, grupo);

        return pagina.Items.FirstOrDefault(m => m.Id == mensagem);
    }

    private async Task<(Guid Grupo, HttpClient Cliente, DizidoApiFactory.Usuario Dono)> GrupoAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var cliente = Api.ClienteDe(dono);

        return ((await CriarGrupoAsync(cliente, "Rapadura Atômica")).Id, cliente, dono);
    }

    private async Task<(Guid Grupo, HttpClient Dono, HttpClient Membro)> GrupoComMembroAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        return (grupo.Id, clienteDoDono, Api.ClienteDe(membro));
    }
}
