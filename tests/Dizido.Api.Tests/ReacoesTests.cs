using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Reactions;

namespace Dizido.Api.Tests;

/// <summary>
/// Reações com emoji. O que se testa aqui é o que o domínio sozinho não alcança: a
/// autorização, a paleta e a promessa de que repetir o mesmo pedido chega no mesmo estado.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class ReacoesTests(DizidoApiFactory api) : TesteDeApi(api)
{
    private const string Polegar = "👍";
    private const string Feito = "✅";

    [Fact]
    public async Task ReagirDevolveQuemReagiu()
    {
        var (grupo, dono, _, idDoMembro) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "combinado?");

        var reacoes = await ReagirAsync(dono, grupo, mensagem.Id, Polegar);

        var unica = Assert.Single(reacoes);
        Assert.Equal(Polegar, unica.Emoji);

        // Quem reagiu foi o dono, e só ele: a lista traz um id, e não é o do membro.
        Assert.NotEqual(idDoMembro, Assert.Single(unica.UserIds));
    }

    /// <summary>Duas pessoas, o mesmo emoji: um grupo só, com os dois dentro.</summary>
    [Fact]
    public async Task DuasPessoasComOMesmoEmojiViramUmGrupoSo()
    {
        var (grupo, dono, membro, idDoMembro) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "sexta às 18h?");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);
        var reacoes = await ReagirAsync(membro, grupo, mensagem.Id, Polegar);

        var unica = Assert.Single(reacoes);
        Assert.Equal(2, unica.UserIds.Count);
        Assert.Contains(idDoMembro, unica.UserIds);
    }

    [Fact]
    public async Task EmojisDiferentesViramGruposDiferentes()
    {
        var (grupo, dono, membro, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "entreguei a build");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);
        var reacoes = await ReagirAsync(membro, grupo, mensagem.Id, Feito);

        Assert.Equal(2, reacoes.Count);
    }

    /// <summary>
    /// A promessa que torna o reenvio seguro: mandar duas vezes o mesmo POST não cria a
    /// segunda reação nem devolve erro. É por isso que pôr e tirar são rotas diferentes, em
    /// vez de um "alternar" que a segunda tentativa desfaria.
    /// </summary>
    [Fact]
    public async Task ReagirDuasVezesComOMesmoEmojiNaoDuplica()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);
        var reacoes = await ReagirAsync(dono, grupo, mensagem.Id, Polegar);

        var unica = Assert.Single(reacoes);
        Assert.Single(unica.UserIds);
    }

    [Fact]
    public async Task TirarAReacaoEsvaziaOGrupo()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);
        var reacoes = await DesreagirAsync(dono, grupo, mensagem.Id, Polegar);

        // O grupo inteiro some quando o último sai — não fica uma bolinha com contagem zero.
        Assert.Empty(reacoes);
    }

    [Fact]
    public async Task TirarUmaReacaoQueNaoExisteNaoEhErro()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        var reacoes = await DesreagirAsync(dono, grupo, mensagem.Id, Polegar);

        Assert.Empty(reacoes);
    }

    /// <summary>Tirar a minha reação não mexe na de mais ninguém.</summary>
    [Fact]
    public async Task TirarASuaNaoTiraADosOutros()
    {
        var (grupo, dono, membro, idDoMembro) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);
        await ReagirAsync(membro, grupo, mensagem.Id, Polegar);

        var reacoes = await DesreagirAsync(dono, grupo, mensagem.Id, Polegar);

        var unica = Assert.Single(reacoes);
        Assert.Equal([idDoMembro], unica.UserIds);
    }

    [Fact]
    public async Task AReacaoSobreviveNoHistorico()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "fica valendo");

        await ReagirAsync(dono, grupo, mensagem.Id, Feito);

        var pagina = await HistoricoAsync(dono, grupo);
        var doHistorico = pagina.Items.First(m => m.Id == mensagem.Id);

        Assert.NotNull(doHistorico.Reactions);
        Assert.Equal(Feito, Assert.Single(doHistorico.Reactions).Emoji);
    }

    /// <summary>
    /// O defeito que este teste existe para impedir: a edição reaproveita o
    /// <c>MessageReceived</c>, então uma resposta de edição sem reações apagaria as reações
    /// da tela de todo mundo.
    /// </summary>
    [Fact]
    public async Task EditarNaoPerdeAsReacoes()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "sexta às 17h");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);

        var resposta = await dono.PatchAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}",
            new EditMessageRequest("sexta às 18h"));

        resposta.EnsureSuccessStatusCode();

        var editada = (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.NotNull(editada.Reactions);
        Assert.Equal(Polegar, Assert.Single(editada.Reactions).Emoji);
    }

    [Fact]
    public async Task MensagemApagadaNaoDevolveReacoes()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "esquece");

        await ReagirAsync(dono, grupo, mensagem.Id, Polegar);

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        var pagina = await HistoricoAsync(dono, grupo);
        var apagada = pagina.Items.First(m => m.Id == mensagem.Id);

        Assert.True(apagada.Reactions is null or { Count: 0 });
    }

    [Fact]
    public async Task MensagemSemReacaoNaoTemLista()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "só falando");

        var pagina = await HistoricoAsync(dono, grupo);
        var doHistorico = pagina.Items.First(m => m.Id == mensagem.Id);

        Assert.True(doHistorico.Reactions is null or { Count: 0 });
    }

    // ----- o que precisa ser recusado -----

    /// <summary>
    /// O teste central da autorização: 404, e não 403, para quem não é da conversa. Um 403
    /// confirmaria que aquela mensagem existe.
    /// </summary>
    [Fact]
    public async Task QuemNaoEhDaConversaNaoReage()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "assunto interno");

        var deFora = Api.ClienteDe(await Api.CriarUsuarioAsync("forasteira"));

        var resposta = await deFora.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}/reactions",
            new ReactRequest(Polegar));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task QuemSaiuDoGrupoNaoReage()
    {
        var (grupo, dono, membro, idDoMembro) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "combinado");

        await ReagirAsync(membro, grupo, mensagem.Id, Polegar);

        (await RemoverAsync(membro, grupo, idDoMembro)).EnsureSuccessStatusCode();

        var resposta = await membro.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}/reactions",
            new ReactRequest(Feito));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    /// <summary>
    /// Um cliente adulterado mandando qualquer texto. Sem a paleta, a coluna do banco vira
    /// campo livre e o balão, um mural.
    /// </summary>
    [Theory]
    [InlineData("🍕")]
    [InlineData("reaja aqui")]
    [InlineData("")]
    [InlineData("❤")]
    public async Task ForaDaPaletaEhRecusado(string emoji)
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}/reactions",
            new ReactRequest(emoji));

        await RecusadoPorRegraAsync(resposta, "paleta");
    }

    /// <summary>
    /// A paleta NÃO vale para tirar: no dia em que um emoji sair da lista, quem já tinha
    /// reagido com ele precisa continuar podendo desfazer — senão ele fica preso no balão.
    /// </summary>
    [Fact]
    public async Task TirarUmEmojiForaDaPaletaNaoEhRecusado()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "ok");

        var resposta = await dono.DeleteAsync(new Uri(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}/reactions?emoji={Uri.EscapeDataString("🍕")}",
            UriKind.Relative));

        resposta.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task NaoDaParaReagirAMensagemApagada()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var mensagem = await MensagemAsync(dono, grupo, "esquece");

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem.Id}/reactions",
            new ReactRequest(Polegar));

        await RecusadoPorRegraAsync(resposta, "apagada");
    }

    [Fact]
    public async Task NaoDaParaReagirAAvisoDoSistema()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();

        // O aviso de "fulano entrou" é a única mensagem que o grupo tem sem ninguém escrever.
        var pagina = await HistoricoAsync(dono, grupo);
        var aviso = pagina.Items.First(m => m.Kind == "System");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{aviso.Id}/reactions",
            new ReactRequest(Polegar));

        await RecusadoPorRegraAsync(resposta, "sistema");
    }

    [Fact]
    public async Task ReagirAMensagemDeOutraConversaEh404()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var outro = await CriarGrupoAsync(dono, "Outro grupo");

        var mensagem = await MensagemAsync(dono, grupo, "aqui");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{outro.Id}/messages/{mensagem.Id}/reactions",
            new ReactRequest(Polegar));

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    // ----- auxiliares -----

    private static async Task<IReadOnlyList<ReactionSummary>> ReagirAsync(
        HttpClient cliente, Guid grupo, Guid mensagem, string emoji)
    {
        var resposta = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages/{mensagem}/reactions",
            new ReactRequest(emoji));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<List<ReactionSummary>>())!;
    }

    private static async Task<IReadOnlyList<ReactionSummary>> DesreagirAsync(
        HttpClient cliente, Guid grupo, Guid mensagem, string emoji)
    {
        var resposta = await cliente.DeleteAsync(new Uri(
            $"/api/conversations/{grupo}/messages/{mensagem}/reactions?emoji={Uri.EscapeDataString(emoji)}",
            UriKind.Relative));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<List<ReactionSummary>>())!;
    }

    private static async Task<MessageResponse> MensagemAsync(
        HttpClient cliente, Guid grupo, string texto)
    {
        var resposta = await EnviarAsync(cliente, grupo, texto);
        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;
    }

    private static async Task<MessagePage> HistoricoAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await LerMensagensAsync(cliente, grupo);
        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessagePage>())!;
    }

    private async Task<(Guid Grupo, HttpClient Dono, HttpClient Membro, Guid IdDoMembro)>
        GrupoComMembroAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        return (grupo.Id, clienteDoDono, Api.ClienteDe(membro), membro.Id);
    }
}
