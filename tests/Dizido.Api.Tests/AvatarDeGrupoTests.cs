using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dizido.Contracts.Attachments;
using Dizido.Contracts.Conversations;

namespace Dizido.Api.Tests;

/// <summary>
/// Avatar do grupo: sobe como qualquer anexo, e a URL é assinada na hora de responder.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class AvatarDeGrupoTests(DizidoApiFactory api) : TesteDeApi(api)
{
    [Fact]
    public async Task ImagemViraAvatarEApareceNaConversa()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var anexo = await SubirImagemAsync(cliente, grupo);

        (await DefinirAvatarAsync(cliente, grupo, anexo.Id)).EnsureSuccessStatusCode();

        var conversa = await VerConversaAsync(cliente, grupo);

        Assert.NotNull(conversa.AvatarUrl);

        // A URL é assinada e aponta para o storage, não para a API — o arquivo nunca passa
        // pelo servidor, nem na volta.
        Assert.Contains("X-Amz-Signature", conversa.AvatarUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// O que a tela usa é a miniatura, não a foto original: o avatar aparece com 38 px de lado.
    /// </summary>
    [Fact]
    public async Task OAvatarUsaAMiniatura()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var anexo = await SubirImagemAsync(cliente, grupo);
        (await DefinirAvatarAsync(cliente, grupo, anexo.Id)).EnsureSuccessStatusCode();

        var conversa = await VerConversaAsync(cliente, grupo);

        Assert.Contains("-thumb", conversa.AvatarUrl!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A razão de o banco guardar o id do anexo e não a URL: a URL assinada expira, e uma
    /// gravada no banco daria um avatar que funciona hoje e quebra amanhã. Aqui, cada resposta
    /// traz uma assinatura nova.
    /// </summary>
    [Fact]
    public async Task CadaRespostaTrazUmaAssinaturaNova()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var anexo = await SubirImagemAsync(cliente, grupo);
        (await DefinirAvatarAsync(cliente, grupo, anexo.Id)).EnsureSuccessStatusCode();

        var primeira = (await VerConversaAsync(cliente, grupo)).AvatarUrl;

        // A assinatura carrega a hora dentro dela; um segundo de diferença já muda o valor.
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        var segunda = (await VerConversaAsync(cliente, grupo)).AvatarUrl;

        Assert.NotNull(primeira);
        Assert.NotNull(segunda);
        Assert.NotEqual(primeira, segunda);
    }

    [Fact]
    public async Task RemoverDeixaAConversaSemImagem()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var anexo = await SubirImagemAsync(cliente, grupo);
        (await DefinirAvatarAsync(cliente, grupo, anexo.Id)).EnsureSuccessStatusCode();

        Assert.NotNull((await VerConversaAsync(cliente, grupo)).AvatarUrl);

        (await DefinirAvatarAsync(cliente, grupo, null)).EnsureSuccessStatusCode();

        Assert.Null((await VerConversaAsync(cliente, grupo)).AvatarUrl);
    }

    [Fact]
    public async Task MembroComumNaoTrocaAImagem()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var membro = await Api.CriarUsuarioAsync("membro");

        var clienteDoDono = Api.ClienteDe(dono);
        var clienteDoMembro = Api.ClienteDe(membro);

        var grupo = (await CriarGrupoAsync(clienteDoDono, "Rapadura Atômica")).Id;
        (await AdicionarAsync(clienteDoDono, grupo, membro.Id)).EnsureSuccessStatusCode();

        var anexo = await SubirImagemAsync(clienteDoMembro, grupo);

        await RecusadoPorRegraAsync(
            await DefinirAvatarAsync(clienteDoMembro, grupo, anexo.Id),
            "administrador");
    }

    [Fact]
    public async Task ArquivoQueNaoEhImagemNaoServeDeAvatar()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.7 nem tento ser imagem");
        var anexo = await SubirAsync(cliente, grupo, "manual.pdf", "application/pdf", pdf);

        await RecusadoPorRegraAsync(await DefinirAvatarAsync(cliente, grupo, anexo.Id), "imagem");
    }

    [Fact]
    public async Task ImagemDeOutraConversaEhRecusada()
    {
        var (aqui, cliente, _) = await GrupoAsync();
        var ali = (await CriarGrupoAsync(cliente, "Outro grupo")).Id;

        var deLa = await SubirImagemAsync(cliente, ali);

        await RecusadoPorRegraAsync(
            await DefinirAvatarAsync(cliente, aqui, deLa.Id),
            "outra conversa");
    }

    [Fact]
    public async Task NaoMembroNaoAlcancaOAvatar()
    {
        var (grupo, _, _) = await GrupoAsync();
        var estranho = Api.ClienteDe(await Api.CriarUsuarioAsync("estranha"));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await DefinirAvatarAsync(estranho, grupo, Guid.CreateVersion7())).StatusCode);
    }

    /// <summary>Trocar a cara do grupo é assunto de quem está nele — vira aviso no fluxo.</summary>
    [Fact]
    public async Task TrocarAImagemGeraAvisoNoFluxo()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var anexo = await SubirImagemAsync(cliente, grupo);
        (await DefinirAvatarAsync(cliente, grupo, anexo.Id)).EnsureSuccessStatusCode();

        var resposta = await LerMensagensAsync(cliente, grupo);
        resposta.EnsureSuccessStatusCode();

        var pagina = (await resposta.Content.ReadFromJsonAsync<Dizido.Contracts.Messages.MessagePage>())!;

        Assert.Contains(pagina.Items, m => m.SystemEvent == "AvatarChanged");
    }

    // ----- auxiliares -----

    private static Task<HttpResponseMessage> DefinirAvatarAsync(
        HttpClient cliente, Guid grupo, Guid? anexo) =>
        cliente.PutAsJsonAsync($"/api/conversations/{grupo}/avatar", new SetGroupAvatarRequest(anexo));

    private static Task<AttachmentResponse> SubirImagemAsync(HttpClient cliente, Guid grupo) =>
        SubirAsync(cliente, grupo, "capa.png", "image/png", PngMinimo);

    /// <summary>Faz os três passos do upload e devolve o anexo pronto.</summary>
    private static async Task<AttachmentResponse> SubirAsync(
        HttpClient cliente, Guid grupo, string nome, string tipo, byte[] conteudo)
    {
        var pedido = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/attachments",
            new RequestUploadRequest(nome, tipo, conteudo.Length));

        pedido.EnsureSuccessStatusCode();

        var bilhete = (await pedido.Content.ReadFromJsonAsync<UploadTicketResponse>())!;

        using (var http = new HttpClient())
        using (var corpo = new ByteArrayContent(conteudo))
        {
            corpo.Headers.ContentType = new MediaTypeHeaderValue(bilhete.ContentType);
            (await http.PutAsync(new Uri(bilhete.UploadUrl), corpo)).EnsureSuccessStatusCode();
        }

        var confirmacao = await cliente.PostAsync(
            new Uri($"/api/attachments/{bilhete.AttachmentId}/complete", UriKind.Relative), null);

        confirmacao.EnsureSuccessStatusCode();

        return (await confirmacao.Content.ReadFromJsonAsync<AttachmentResponse>())!;
    }

    private static async Task<ConversationResponse> VerConversaAsync(HttpClient cliente, Guid grupo)
    {
        var resposta = await VerAsync(cliente, grupo);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<ConversationResponse>())!;
    }

    private async Task<(Guid Grupo, HttpClient Cliente, DizidoApiFactory.Usuario Dono)> GrupoAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var cliente = Api.ClienteDe(dono);

        return ((await CriarGrupoAsync(cliente, "Rapadura Atômica")).Id, cliente, dono);
    }
}
