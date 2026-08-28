using System.Net;

namespace Dizido.Api.Tests;

/// <summary>
/// O perímetro: quem consegue chegar perto de uma conversa, e o que o servidor responde a
/// quem não deveria.
/// </summary>
/// <remarks>
/// Estas regras eram verificadas à mão com curl até aqui. São exatamente as que quebram em
/// silêncio numa refatoração: nenhuma tela deixa de funcionar quando um 404 vira 403 ou quando
/// um endpoint novo esquece o <c>RequireAuthorization</c> — só passa a vazar.
/// </remarks>
[Collection(ColecaoDaApi.Nome)]
public sealed class AcessoAConversaTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task SemTokenNaoPassaDaPorta()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var grupo = await CriarGrupoAsync(Api.ClienteDe(dono), "Rapadura Atômica");

        // Cliente sem cabeçalho Authorization nenhum.
        var anonimo = Api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await VerAsync(anonimo, grupo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LerMensagensAsync(anonimo, grupo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenomearAsync(anonimo, grupo.Id, "Outro")).StatusCode);
    }

    [Fact]
    public async Task TokenInvalidoNaoPassaDaPorta()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var grupo = await CriarGrupoAsync(Api.ClienteDe(dono), "Rapadura Atômica");

        var falsificado = Api.ClienteDe(dono with { Token = dono.Token + "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await VerAsync(falsificado, grupo.Id)).StatusCode);
    }

    /// <summary>
    /// A regra do 404 uniforme: para quem não participa, a conversa é indistinguível de uma
    /// que não existe. Um 403 confirmaria que aquele id é real.
    /// </summary>
    [Fact]
    public async Task NaoMembroRecebeOMesmo404QueUmIdInexistente()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var estranho = await Api.CriarUsuarioAsync("estranha");

        var grupo = await CriarGrupoAsync(Api.ClienteDe(dono), "Rapadura Atômica");
        var cliente = Api.ClienteDe(estranho);

        var doGrupoQueExiste = await VerAsync(cliente, grupo.Id);
        var deUmIdQualquer = await VerAsync(cliente, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, doGrupoQueExiste.StatusCode);
        Assert.Equal(deUmIdQualquer.StatusCode, doGrupoQueExiste.StatusCode);
    }

    [Fact]
    public async Task NaoMembroNaoLeNemEscreveNaConversa()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var estranho = await Api.CriarUsuarioAsync("estranha");

        var grupo = await CriarGrupoAsync(Api.ClienteDe(dono), "Rapadura Atômica");
        var cliente = Api.ClienteDe(estranho);

        Assert.Equal(HttpStatusCode.NotFound, (await LerMensagensAsync(cliente, grupo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(cliente, grupo.Id, "oi")).StatusCode);
    }

    [Fact]
    public async Task NaoMembroNaoAdministraOGrupo()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var estranho = await Api.CriarUsuarioAsync("estranha");
        var alvo = await Api.CriarUsuarioAsync("alvo");

        var grupo = await CriarGrupoAsync(Api.ClienteDe(dono), "Rapadura Atômica");
        var cliente = Api.ClienteDe(estranho);

        // 404 e não 400: o servidor nem chega a consultar a permissão, porque para este
        // usuário a conversa não existe. É a diferença entre "você não pode" e "o quê?".
        Assert.Equal(HttpStatusCode.NotFound, (await RenomearAsync(cliente, grupo.Id, "Sequestrado")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdicionarAsync(cliente, grupo.Id, alvo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RemoverAsync(cliente, grupo.Id, dono.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TransferirAsync(cliente, grupo.Id, estranho.Id)).StatusCode);
    }

    /// <summary>
    /// Sair do grupo não é só deixar de aparecer na lista: o acesso ao histórico acaba junto.
    /// </summary>
    /// <remarks>
    /// O membro continua na tabela, com <c>LeftAt</c> preenchido — a linha é preservada para
    /// que os avisos de sistema antigos ainda consigam resolver o nome dele. Por isso o teste
    /// importa: uma consulta que esquecesse o <c>LeftAt == null</c> devolveria acesso a quem
    /// já saiu, e nada na tela denunciaria isso.
    /// </remarks>
    [Fact]
    public async Task QuemSaiPerdeOAcessoAoHistorico()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var clienteDoMembro = Api.ClienteDe(membro);

        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");
        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await VerAsync(clienteDoMembro, grupo.Id)).StatusCode);

        // Remover a si mesmo é sair — e isso qualquer membro pode.
        (await RemoverAsync(clienteDoMembro, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await VerAsync(clienteDoMembro, grupo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LerMensagensAsync(clienteDoMembro, grupo.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EnviarAsync(clienteDoMembro, grupo.Id, "ainda estou aqui?")).StatusCode);
    }
}
