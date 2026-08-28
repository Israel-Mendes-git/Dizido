using System.Net.Http.Json;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Busca no histórico. O teste mais importante daqui é o do vazamento: procurar uma palavra
/// não pode devolver mensagem de conversa alheia.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class BuscaTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task EncontraPelaPalavra()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        await EnviarAsync(cliente, grupo, "a reunião de quarta foi adiada");
        await EnviarAsync(cliente, grupo, "assunto totalmente diferente");

        var achados = await BuscarAsync(cliente, "reunião");

        Assert.Single(achados.Items);
        Assert.Contains("adiada", achados.Items[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// O radical das palavras: procurar por "correr" precisa achar "correndo". É o que a
    /// configuração 'portuguese' do Postgres traz, e o que faltaria com 'simple'.
    /// </summary>
    [Fact]
    public async Task EncontraPelaRaizDaPalavra()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        await EnviarAsync(cliente, grupo, "estou correndo para terminar isso");

        var achados = await BuscarAsync(cliente, "correr");

        Assert.Single(achados.Items);
    }

    [Fact]
    public async Task NaoDiferenciaMaiusculaNemAcento()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        await EnviarAsync(cliente, grupo, "Combinamos ÀS CINCO");

        Assert.Single((await BuscarAsync(cliente, "combinamos")).Items);
    }

    /// <summary>
    /// O teste que justifica o recurso existir com cuidado: a busca varre o histórico
    /// <b>inteiro</b> do banco, e sem o filtro por participação devolveria conversa dos outros.
    /// </summary>
    [Fact]
    public async Task NuncaDevolveMensagemDeConversaAlheia()
    {
        var (_, dele, _) = await GrupoAsync();
        var estranho = Api.ClienteDe(await Api.CriarUsuarioAsync("estranha"));

        var palavraRara = $"xilofone{Guid.NewGuid():N}";
        await EnviarAsync(dele, (await ConversaDeAsync(dele)), $"combinado sobre {palavraRara}");

        // O estranho procura pela mesma palavra rara, que só existe na conversa do outro.
        var achados = await BuscarAsync(estranho, palavraRara);

        Assert.Empty(achados.Items);
    }

    [Fact]
    public async Task QuemSaiuDoGrupoNaoBuscaMaisNele()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var clienteDoMembro = Api.ClienteDe(membro);

        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");
        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        var palavraRara = $"berimbau{Guid.NewGuid():N}";
        await EnviarAsync(clienteDoDono, grupo.Id, $"algo sobre {palavraRara}");

        Assert.Single((await BuscarAsync(clienteDoMembro, palavraRara)).Items);

        (await RemoverAsync(clienteDoMembro, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        Assert.Empty((await BuscarAsync(clienteDoMembro, palavraRara)).Items);
    }

    [Fact]
    public async Task PodeLimitarAUmaConversa()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var cliente = Api.ClienteDe(dono);

        var aqui = await CriarGrupoAsync(cliente, "Aqui");
        var ali = await CriarGrupoAsync(cliente, "Ali");

        var palavraRara = $"cavaquinho{Guid.NewGuid():N}";
        await EnviarAsync(cliente, aqui.Id, $"assunto {palavraRara}");
        await EnviarAsync(cliente, ali.Id, $"outro {palavraRara}");

        Assert.Equal(2, (await BuscarAsync(cliente, palavraRara)).Items.Count);

        var soAqui = await BuscarAsync(cliente, palavraRara, aqui.Id);

        Assert.Single(soAqui.Items);
        Assert.Equal(aqui.Id, soAqui.Items[0].ConversationId);
    }

    [Fact]
    public async Task MensagemApagadaSaiDosResultados()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var palavraRara = $"pandeiro{Guid.NewGuid():N}";
        var resposta = await EnviarAsync(cliente, grupo, $"vai sumir {palavraRara}");
        var mensagem = (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.Single((await BuscarAsync(cliente, palavraRara)).Items);

        (await cliente.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        Assert.Empty((await BuscarAsync(cliente, palavraRara)).Items);
    }

    [Fact]
    public async Task AvisoDoSistemaNaoApareceNaBusca()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var cliente = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(cliente, "Rapadura Atômica");

        (await AdicionarAsync(cliente, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        // O aviso de "entrou no grupo" guarda o código do evento, não texto — mas mesmo que
        // guardasse, a busca não devolve mensagens de sistema.
        var achados = await BuscarAsync(cliente, "grupo");

        Assert.DoesNotContain(achados.Items, m => m.Kind == "System");
    }

    /// <summary>
    /// Texto digitado por gente entra como está. Com to_tsquery no lugar de plainto_tsquery,
    /// um apóstrofo ou um "&amp;" solto viraria erro 500.
    /// </summary>
    [Theory]
    [InlineData("é isso aí!")]
    [InlineData("a & b")]
    [InlineData("aspas 'simples' e \"duplas\"")]
    [InlineData("parênteses ( sem fechar")]
    [InlineData("acentuação: ção, ãe, ôo")]
    public async Task PontuacaoNaoQuebraABusca(string termo)
    {
        var (_, cliente, _) = await GrupoAsync();

        var resposta = await cliente.GetAsync(
            new Uri($"/api/search?q={Uri.EscapeDataString(termo)}", UriKind.Relative));

        resposta.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TermoCurtoDemaisDevolveVazio()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        await EnviarAsync(cliente, grupo, "a palavra a aparece muito");

        Assert.Empty((await BuscarAsync(cliente, "a")).Items);
        Assert.Empty((await BuscarAsync(cliente, "")).Items);
    }

    // ----- auxiliares -----

    private static async Task<MessagePage> BuscarAsync(
        HttpClient cliente, string termo, Guid? conversa = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(termo)}"
                  + (conversa is null ? string.Empty : $"&conversationId={conversa}");

        var resposta = await cliente.GetAsync(new Uri(url, UriKind.Relative));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessagePage>())!;
    }

    private static async Task<Guid> ConversaDeAsync(HttpClient cliente)
    {
        var resposta = await cliente.GetAsync(new Uri("/api/conversations", UriKind.Relative));

        resposta.EnsureSuccessStatusCode();

        var conversas = await resposta.Content.ReadFromJsonAsync<List<Dizido.Contracts.Conversations.ConversationResponse>>();

        return conversas![0].Id;
    }

    private async Task<(Guid Grupo, HttpClient Cliente, DizidoApiFactory.Usuario Dono)> GrupoAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var cliente = Api.ClienteDe(dono);

        return ((await CriarGrupoAsync(cliente, "Rapadura Atômica")).Id, cliente, dono);
    }
}
