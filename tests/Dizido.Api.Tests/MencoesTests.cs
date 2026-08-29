using System.Net.Http.Json;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Menções. O teste que importa é o do estranho: citar alguém é notificá-lo, e sem regra
/// isso vira um jeito de incomodar quem nem participa da conversa.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class MencoesTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task CitarUmParticipanteFunciona()
    {
        var (grupo, dono, _, idDoMembro) = await GrupoComMembroAsync();

        var mensagem = await CitarAsync(dono, grupo, "olha isso @membro", [idDoMembro]);

        Assert.NotNull(mensagem.MentionedUserIds);
        Assert.Equal([idDoMembro], mensagem.MentionedUserIds);
    }

    [Fact]
    public async Task ACitacaoSobreviveNoHistorico()
    {
        var (grupo, dono, _, idDoMembro) = await GrupoComMembroAsync();

        await CitarAsync(dono, grupo, "atenção @membro", [idDoMembro]);

        var resposta = await LerMensagensAsync(dono, grupo);
        resposta.EnsureSuccessStatusCode();

        var pagina = (await resposta.Content.ReadFromJsonAsync<MessagePage>())!;
        var doHistorico = pagina.Items.First(m => m.Body.Contains("atenção", StringComparison.Ordinal));

        Assert.Equal([idDoMembro], doHistorico.MentionedUserIds);
    }

    /// <summary>
    /// O teste central: um cliente adulterado mandando o id de quem não está na conversa.
    /// </summary>
    [Fact]
    public async Task NaoDaParaCitarQuemNaoEstaNaConversa()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();
        var deFora = await Api.CriarUsuarioAsync("forasteira");

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(
                Guid.CreateVersion7(), "oi @forasteira", MentionedUserIds: [deFora.Id]));

        await RecusadoPorRegraAsync(resposta, "participantes da conversa");
    }

    [Fact]
    public async Task NaoDaParaCitarQuemJaSaiu()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica");

        (await AdicionarAsync(clienteDoDono, grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        // Enquanto está dentro, dá.
        await CitarAsync(clienteDoDono, grupo.Id, "@membro", [membro.Id]);

        (await RemoverAsync(Api.ClienteDe(membro), grupo.Id, membro.Id)).EnsureSuccessStatusCode();

        var resposta = await clienteDoDono.PostAsJsonAsync(
            $"/api/conversations/{grupo.Id}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), "@membro?", MentionedUserIds: [membro.Id]));

        await RecusadoPorRegraAsync(resposta, "participantes da conversa");
    }

    [Fact]
    public async Task CitarAMesmaPessoaDuasVezesEhRecusado()
    {
        var (grupo, dono, _, idDoMembro) = await GrupoComMembroAsync();

        var resposta = await dono.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(
                Guid.CreateVersion7(), "@membro @membro", MentionedUserIds: [idDoMembro, idDoMembro]));

        await RecusadoPorRegraAsync(resposta, "mais de uma vez");
    }

    [Fact]
    public async Task MensagemSemMencaoNaoTemLista()
    {
        var (grupo, dono, _, _) = await GrupoComMembroAsync();

        var resposta = await EnviarAsync(dono, grupo, "sem citar ninguém");
        resposta.EnsureSuccessStatusCode();

        var mensagem = (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.True(mensagem.MentionedUserIds is null or { Count: 0 });
    }

    /// <summary>
    /// Apagar tira as menções junto: destacar um nome numa mensagem que não existe mais só
    /// confundiria.
    /// </summary>
    [Fact]
    public async Task MensagemApagadaNaoDevolveMencoes()
    {
        var (grupo, dono, _, idDoMembro) = await GrupoComMembroAsync();

        var mensagem = await CitarAsync(dono, grupo, "@membro veja", [idDoMembro]);

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{mensagem.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        var resposta = await LerMensagensAsync(dono, grupo);
        var pagina = (await resposta.Content.ReadFromJsonAsync<MessagePage>())!;
        var apagada = pagina.Items.First(m => m.Id == mensagem.Id);

        Assert.True(apagada.MentionedUserIds is null or { Count: 0 });
    }

    // ----- auxiliares -----

    private static async Task<MessageResponse> CitarAsync(
        HttpClient cliente, Guid grupo, string texto, IReadOnlyList<Guid> citados)
    {
        var resposta = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), texto, MentionedUserIds: citados));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;
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
