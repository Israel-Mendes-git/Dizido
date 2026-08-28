using System.Net.Http.Json;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// Responder a uma mensagem: a citação é montada pelo servidor, não recebida do cliente.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class ResponderTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task ARespostaCarregaOAutorEOTrechoDoOriginal()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var original = await EnviarEObterAsync(dono, grupo, "alguém sabe onde parou aquilo?");
        var resposta = await ResponderAsync(membro, grupo, original.Id, "parou comigo");

        Assert.NotNull(resposta.ReplyTo);
        Assert.Equal(original.Id, resposta.ReplyTo.MessageId);
        Assert.Equal(original.SenderDisplayName, resposta.ReplyTo.SenderDisplayName);
        Assert.Equal("alguém sabe onde parou aquilo?", resposta.ReplyTo.Excerpt);
        Assert.False(resposta.ReplyTo.IsDeleted);
    }

    /// <summary>
    /// A citação sobrevive ao histórico: quem carrega a página muito depois continua vendo
    /// a que a resposta se refere, mesmo sem a original na mesma página.
    /// </summary>
    [Fact]
    public async Task ACitacaoVemJuntoNoHistorico()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var original = await EnviarEObterAsync(dono, grupo, "pergunta");
        await ResponderAsync(dono, grupo, original.Id, "resposta");

        var pagina = await LerPaginaAsync(dono, grupo);
        var doHistorico = pagina.Items.First(m => m.Body == "resposta");

        Assert.Equal("pergunta", doHistorico.ReplyTo?.Excerpt);
    }

    [Fact]
    public async Task TrechoLongoEhCortado()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var textao = new string('a', 300);
        var original = await EnviarEObterAsync(dono, grupo, textao);
        var resposta = await ResponderAsync(dono, grupo, original.Id, "curto");

        Assert.EndsWith("…", resposta.ReplyTo!.Excerpt, StringComparison.Ordinal);
        Assert.True(resposta.ReplyTo.Excerpt.Length < 100);
    }

    /// <summary>
    /// Apagar o original não quebra a resposta — é a razão de o apagamento ser suave.
    /// </summary>
    [Fact]
    public async Task ApagarOOriginalDeixaACitacaoMarcada()
    {
        var (grupo, dono, _) = await GrupoComMembroAsync();

        var original = await EnviarEObterAsync(dono, grupo, "vai sumir");
        await ResponderAsync(dono, grupo, original.Id, "respondi antes");

        (await dono.DeleteAsync(
            new Uri($"/api/conversations/{grupo}/messages/{original.Id}", UriKind.Relative)))
            .EnsureSuccessStatusCode();

        var pagina = await LerPaginaAsync(dono, grupo);
        var resposta = pagina.Items.First(m => m.Body == "respondi antes");

        Assert.True(resposta.ReplyTo?.IsDeleted);
        Assert.Equal("mensagem apagada", resposta.ReplyTo!.Excerpt);
    }

    /// <summary>
    /// O cliente manda só o id do original; quem monta o texto da citação é o servidor.
    /// </summary>
    /// <remarks>
    /// É o que impede alguém de publicar uma citação forjada — "Fulano disse X" com um X que
    /// Fulano nunca escreveu — que apareceria como legítima para todo mundo do grupo.
    /// </remarks>
    [Fact]
    public async Task OClienteNaoEscolheOTextoDaCitacao()
    {
        var (grupo, dono, membro) = await GrupoComMembroAsync();

        var original = await EnviarEObterAsync(dono, grupo, "o que eu realmente disse");
        var resposta = await ResponderAsync(membro, grupo, original.Id, "pois é");

        // O contrato de envio (SendMessageRequest) nem tem campo para o texto da citação:
        // só ReplyToMessageId. Este teste registra a garantia por comportamento.
        Assert.Equal("o que eu realmente disse", resposta.ReplyTo!.Excerpt);
    }

    // ----- auxiliares -----

    private static async Task<MessageResponse> ResponderAsync(
        HttpClient cliente, Guid grupo, Guid original, string texto)
    {
        var resposta = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), texto, ReplyToMessageId: original));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<MessageResponse>())!;
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
