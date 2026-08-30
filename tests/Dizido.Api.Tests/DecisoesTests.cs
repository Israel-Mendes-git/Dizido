using System.Net;
using System.Net.Http.Json;
using Dizido.Contracts.Decisions;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Decisões registradas a partir de mensagens.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class DecisoesTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task RegistrarGuardaOResumoEOElo()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var mensagem = await EnviarEObterAsync(dono, grupo, "então fica assim mesmo");

        var decisao = await RegistrarAsync(
            dono, grupo, mensagem.Id, "stamina volta ao sistema antigo: o novo confundia no teste");

        Assert.Equal(mensagem.Id, decisao.MessageId);
        Assert.Contains("stamina", decisao.Summary, StringComparison.Ordinal);

        // O trecho da mensagem original é o elo com a discussão — é o que separa isto de um
        // documento escrito fora de contexto.
        Assert.Equal("então fica assim mesmo", decisao.MessageExcerpt);
    }

    /// <summary>
    /// Qualquer participante registra, não só administradores: quem percebe que algo ficou
    /// decidido raramente é quem manda no grupo.
    /// </summary>
    [Fact]
    public async Task MembroComumTambemRegistra()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var mensagem = await EnviarEObterAsync(dono, grupo, "combinado");

        var decisao = await RegistrarAsync(membro, grupo, mensagem.Id, "ficou combinado assim");

        Assert.NotEqual(Guid.Empty, decisao.Id);
    }

    [Fact]
    public async Task RegistrarGeraAvisoNoFluxo()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var mensagem = await EnviarEObterAsync(dono, grupo, "vale");
        await RegistrarAsync(dono, grupo, mensagem.Id, "o formato do save muda na próxima build");

        var pagina = await LerPaginaAsync(dono, grupo);
        var aviso = pagina.Items.FirstOrDefault(m => m.SystemEvent == "DecisionRegistered");

        Assert.NotNull(aviso);

        // Este é o único aviso de sistema que carrega texto próprio: o resumo é escrito por
        // uma pessoa e não tem como ser remontado a partir de um código.
        Assert.Contains("formato do save", aviso.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumoVazioEhRecusado()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(dono, grupo, "algo");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/decisions",
            new RegisterDecisionRequest(mensagem.Id, "   "));

        await RecusadoPorRegraAsync(resposta, "Escreva o que ficou decidido");
    }

    /// <summary>Uma mensagem vira decisão uma vez só — dois cliques rápidos não duplicam.</summary>
    [Fact]
    public async Task AMesmaMensagemNaoViraDuasDecisoes()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();
        var mensagem = await EnviarEObterAsync(dono, grupo, "combinado");

        await RegistrarAsync(dono, grupo, mensagem.Id, "primeira");

        var segunda = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/decisions",
            new RegisterDecisionRequest(mensagem.Id, "segunda"));

        Assert.False(segunda.IsSuccessStatusCode);
    }

    [Fact]
    public async Task AvisoDoSistemaNaoViraDecisao()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var pagina = await LerPaginaAsync(dono, grupo);
        var aviso = pagina.Items.First(m => m.Kind == "System");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/decisions",
            new RegisterDecisionRequest(aviso.Id, "não deveria dar"));

        await RecusadoPorRegraAsync(resposta, "sistema");
    }

    [Fact]
    public async Task NaoMembroNaoAlcancaAsDecisoes()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();
        var estranho = Api.ClienteDe(await Api.CriarUsuarioAsync("estranha"));

        var mensagem = await EnviarEObterAsync(dono, grupo, "assunto interno");
        await RegistrarAsync(dono, grupo, mensagem.Id, "resolvido internamente");

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await estranho.GetAsync(new Uri($"/api/conversations/{grupo}/decisions", UriKind.Relative))).StatusCode);
    }

    /// <summary>
    /// A corrente: revisar não apaga a anterior. "Decidido em março, revisto em agosto, e o
    /// motivo de cada uma" é o que impede a equipe de refazer a mesma discussão.
    /// </summary>
    [Fact]
    public async Task RevisarFormaUmaCorrente()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var primeiraMensagem = await EnviarEObterAsync(dono, grupo, "vai ser em março");
        var antiga = await RegistrarAsync(dono, grupo, primeiraMensagem.Id, "lançamento em março");

        var segundaMensagem = await EnviarEObterAsync(dono, grupo, "adiamos");
        var nova = await RegistrarAsync(
            dono, grupo, segundaMensagem.Id, "lançamento passa para junho: faltou tempo de teste", antiga.Id);

        // Por padrão o painel mostra só o que vale.
        var ativas = await ListarAsync(dono, grupo);
        Assert.Single(ativas);
        Assert.Equal(nova.Id, ativas[0].Id);

        // Com o filtro, a corrente inteira aparece.
        var todas = await ListarAsync(dono, grupo, incluirRevistas: true);
        Assert.Equal(2, todas.Count);

        var revista = todas.First(d => d.Id == antiga.Id);
        Assert.Equal(nova.Id, revista.SupersededByDecisionId);
    }

    [Fact]
    public async Task NaoDaParaRevisarDuasVezesAMesmaDecisao()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var m1 = await EnviarEObterAsync(dono, grupo, "um");
        var antiga = await RegistrarAsync(dono, grupo, m1.Id, "primeira versão");

        var m2 = await EnviarEObterAsync(dono, grupo, "dois");
        await RegistrarAsync(dono, grupo, m2.Id, "segunda versão", antiga.Id);

        var m3 = await EnviarEObterAsync(dono, grupo, "três");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/decisions",
            new RegisterDecisionRequest(m3.Id, "terceira versão", antiga.Id));

        await RecusadoPorRegraAsync(resposta, "já foi revista");
    }

    /// <summary>Desfazer é para corrigir o próprio engano, não é moderação.</summary>
    [Fact]
    public async Task SoQuemRegistrouDesfaz()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var mensagem = await EnviarEObterAsync(dono, grupo, "algo");
        var decisao = await RegistrarAsync(membro, grupo, mensagem.Id, "registrei eu");

        var caminho = new Uri($"/api/conversations/{grupo}/decisions/{decisao.Id}", UriKind.Relative);

        // Nem o dono do grupo desfaz o registro de outra pessoa.
        Assert.Equal(HttpStatusCode.NotFound, (await dono.DeleteAsync(caminho)).StatusCode);

        (await membro.DeleteAsync(caminho)).EnsureSuccessStatusCode();

        Assert.Empty(await ListarAsync(dono, grupo));
    }

    /// <summary>
    /// A decisão sobrevive ao apagamento da mensagem — é por isso que o resumo é escrito à
    /// mão em vez de copiado do corpo.
    /// </summary>
    [Fact]
    public async Task ApagarAMensagemNaoApagaADecisao()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var mensagem = await EnviarEObterAsync(dono, grupo, "vou apagar isto depois");
        await RegistrarAsync(dono, grupo, mensagem.Id, "o combinado continua valendo");

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        var decisoes = await ListarAsync(dono, grupo);

        Assert.Single(decisoes);
        Assert.Equal("o combinado continua valendo", decisoes[0].Summary);
        Assert.Equal("mensagem apagada", decisoes[0].MessageExcerpt);
    }

    // ----- auxiliares -----

    private static async Task<DecisionResponse> RegistrarAsync(
        HttpClient cliente, Guid grupo, Guid mensagem, string resumo, Guid? revendo = null)
    {
        var resposta = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/decisions",
            new RegisterDecisionRequest(mensagem, resumo, revendo));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<DecisionResponse>())!;
    }

    private static async Task<List<DecisionResponse>> ListarAsync(
        HttpClient cliente, Guid grupo, bool incluirRevistas = false)
    {
        var resposta = await cliente.GetAsync(
            new Uri($"/api/conversations/{grupo}/decisions?incluirRevistas={incluirRevistas}", UriKind.Relative));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<List<DecisionResponse>>())!;
    }

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
