using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dizido.Contracts.Attachments;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;

namespace Dizido.Api.Tests;

/// <summary>
/// O upload em três passos, de ponta a ponta: contra um MinIO de verdade, com os bytes
/// subindo direto para ele, sem passar pela API.
/// </summary>
[Collection(ColecaoDaApi.Nome)]
public sealed class AnexoTests(DizidoApiFactory api) : TesteDeApi(api)
{
    /// <summary>Um PNG de 1x1 pixel, válido de verdade — o Skia consegue abrir e reduzir.</summary>
    private static readonly byte[] PngMinimo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public async Task ImagemSobeDiretoParaOStorageEViraMensagem()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);

        // O PUT vai para o MinIO, não para a API. É este passo que tira o servidor do caminho.
        Assert.Equal(HttpStatusCode.OK, (await SubirAsync(bilhete, PngMinimo)).StatusCode);

        var anexo = await ConfirmarAsync(cliente, bilhete.AttachmentId);

        Assert.Equal("Image", anexo.Kind);
        Assert.Equal("image/png", anexo.ContentType);
        Assert.Equal(PngMinimo.Length, anexo.SizeBytes);
        Assert.Equal(1, anexo.Width);
        Assert.Equal(1, anexo.Height);
        Assert.NotNull(anexo.ThumbnailUrl);

        // Foto sem legenda: o corpo vai vazio e a mensagem continua válida.
        var envio = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), string.Empty, AttachmentId: anexo.Id));

        envio.EnsureSuccessStatusCode();

        var mensagem = (await envio.Content.ReadFromJsonAsync<MessageResponse>())!;

        Assert.NotNull(mensagem.Attachment);
        Assert.Equal(anexo.Id, mensagem.Attachment.Id);
        Assert.Equal("gato.png", mensagem.Attachment.FileName);
    }

    /// <summary>
    /// O caso que justifica o terceiro passo existir: o cliente pede para subir um PNG e
    /// sobe um HTML. Se passasse, o arquivo seria servido inline na origem do app.
    /// </summary>
    [Fact]
    public async Task HtmlDisfarcadoDeImagemEhRecusadoEApagado()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var html = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");
        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", html.Length);

        // O storage aceita: para ele são bytes com o Content-Type que a assinatura exigia.
        (await SubirAsync(bilhete, html)).EnsureSuccessStatusCode();

        var confirmacao = await cliente.PostAsync(
            new Uri($"/api/attachments/{bilhete.AttachmentId}/complete", UriKind.Relative), null);

        await RecusadoPorRegraAsync(confirmacao, "não é uma imagem");

        // E o anexo recusado não fica para trás: nem no banco, nem no bucket.
        var depois = await cliente.GetAsync(
            new Uri($"/api/attachments/{bilhete.AttachmentId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, depois.StatusCode);
    }

    [Fact]
    public async Task ArquivoComumNaoPrecisaSerImagem()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.7 conteúdo qualquer");
        var bilhete = await PedirUploadAsync(cliente, grupo, "relatório final.pdf", "application/pdf", pdf.Length);

        // Arquivo comum sobe como octet-stream, mesmo tendo sido declarado como PDF.
        Assert.Equal("application/octet-stream", bilhete.ContentType);

        (await SubirAsync(bilhete, pdf)).EnsureSuccessStatusCode();

        var anexo = await ConfirmarAsync(cliente, bilhete.AttachmentId);

        Assert.Equal("File", anexo.Kind);
        Assert.Null(anexo.ThumbnailUrl);
        Assert.Null(anexo.Width);
        Assert.Equal("relatório final.pdf", anexo.FileName);
    }

    [Fact]
    public async Task ImagemGrandeDemaisEhRecusadaAntesDeSubir()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var pedido = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/attachments",
            new RequestUploadRequest("enorme.png", "image/png", 11L * 1024 * 1024));

        await RecusadoPorRegraAsync(pedido, "10 MB");
    }

    [Fact]
    public async Task ConfirmarSemTerSubidoNadaDaConflito()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);

        var confirmacao = await cliente.PostAsync(
            new Uri($"/api/attachments/{bilhete.AttachmentId}/complete", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, confirmacao.StatusCode);
    }

    /// <summary>
    /// A resposta do "complete" pode se perder na volta. Repetir precisa devolver o mesmo
    /// resultado, senão o retry do cliente quebra o envio.
    /// </summary>
    [Fact]
    public async Task ConfirmarDuasVezesDevolveOMesmoAnexo()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);
        (await SubirAsync(bilhete, PngMinimo)).EnsureSuccessStatusCode();

        var primeira = await ConfirmarAsync(cliente, bilhete.AttachmentId);
        var segunda = await ConfirmarAsync(cliente, bilhete.AttachmentId);

        Assert.Equal(primeira.Id, segunda.Id);
        Assert.Equal(primeira.SizeBytes, segunda.SizeBytes);
    }

    [Fact]
    public async Task NaoMembroNaoPedeUploadNaConversa()
    {
        var (grupo, _, _) = await GrupoAsync();
        var estranho = await Api.CriarUsuarioAsync("estranha");

        var pedido = await Api.ClienteDe(estranho).PostAsJsonAsync(
            $"/api/conversations/{grupo}/attachments",
            new RequestUploadRequest("gato.png", "image/png", 100));

        Assert.Equal(HttpStatusCode.NotFound, pedido.StatusCode);
    }

    [Fact]
    public async Task SoQuemPediuOUploadConfirma()
    {
        var (grupo, cliente, dono) = await GrupoAsync();
        var outro = await Api.CriarUsuarioAsync("outra");

        (await AdicionarAsync(cliente, grupo, outro.Id)).EnsureSuccessStatusCode();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);
        (await SubirAsync(bilhete, PngMinimo)).EnsureSuccessStatusCode();

        // Membro do mesmo grupo, mas não foi ele quem pediu o upload.
        var confirmacao = await Api.ClienteDe(outro).PostAsync(
            new Uri($"/api/attachments/{bilhete.AttachmentId}/complete", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, confirmacao.StatusCode);

        // O dono do upload continua conseguindo.
        Assert.NotNull(dono);
        await ConfirmarAsync(cliente, bilhete.AttachmentId);
    }

    /// <summary>
    /// A URL de leitura é renovável por quem participa da conversa do anexo — e só por ele.
    /// </summary>
    [Fact]
    public async Task MembroRenovaAUrlEEstranhoNao()
    {
        var (grupo, cliente, _) = await GrupoAsync();
        var membro = await Api.CriarUsuarioAsync("membro");
        var estranho = await Api.CriarUsuarioAsync("estranha");

        (await AdicionarAsync(cliente, grupo, membro.Id)).EnsureSuccessStatusCode();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);
        (await SubirAsync(bilhete, PngMinimo)).EnsureSuccessStatusCode();
        await ConfirmarAsync(cliente, bilhete.AttachmentId);

        var caminho = new Uri($"/api/attachments/{bilhete.AttachmentId}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await Api.ClienteDe(membro).GetAsync(caminho)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.ClienteDe(estranho).GetAsync(caminho)).StatusCode);
    }

    /// <summary>
    /// Republicar numa conversa sua um arquivo de outra. O domínio recusa; aqui verificamos
    /// que o endpoint chega até essa recusa.
    /// </summary>
    [Fact]
    public async Task AnexoDeOutraConversaNaoViraMensagemAqui()
    {
        var (origem, cliente, _) = await GrupoAsync();
        var destino = await CriarGrupoAsync(cliente, "Outro grupo");

        var bilhete = await PedirUploadAsync(cliente, origem, "gato.png", "image/png", PngMinimo.Length);
        (await SubirAsync(bilhete, PngMinimo)).EnsureSuccessStatusCode();
        var anexo = await ConfirmarAsync(cliente, bilhete.AttachmentId);

        var envio = await cliente.PostAsJsonAsync(
            $"/api/conversations/{destino.Id}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), "olha", AttachmentId: anexo.Id));

        await RecusadoPorRegraAsync(envio, "outra conversa");
    }

    [Fact]
    public async Task MensagemPendenteNaoViraMensagem()
    {
        var (grupo, cliente, _) = await GrupoAsync();

        var bilhete = await PedirUploadAsync(cliente, grupo, "gato.png", "image/png", PngMinimo.Length);

        // Sem subir e sem confirmar: o anexo existe no banco, mas não vale.
        var envio = await cliente.PostAsJsonAsync(
            $"/api/conversations/{grupo}/messages",
            new SendMessageRequest(Guid.CreateVersion7(), "olha", AttachmentId: bilhete.AttachmentId));

        await RecusadoPorRegraAsync(envio, "ainda não terminou");
    }

    private async Task<(Guid Grupo, HttpClient Cliente, DizidoApiFactory.Usuario Dono)> GrupoAsync()
    {
        var dono = await Api.CriarUsuarioAsync("dona");
        var cliente = Api.ClienteDe(dono);
        var grupo = await CriarGrupoAsync(cliente, "Rapadura Atômica");

        return (grupo.Id, cliente, dono);
    }

    private static async Task<UploadTicketResponse> PedirUploadAsync(
        HttpClient cliente, Guid conversa, string nome, string tipo, long tamanho)
    {
        var resposta = await cliente.PostAsJsonAsync(
            $"/api/conversations/{conversa}/attachments",
            new RequestUploadRequest(nome, tipo, tamanho));

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<UploadTicketResponse>())!;
    }

    /// <summary>
    /// Faz o <c>PUT</c> no storage exatamente como o navegador faria.
    /// </summary>
    /// <remarks>
    /// Um <see cref="HttpClient"/> comum, e não o do servidor de teste: a URL aponta para o
    /// MinIO, que é outro processo. Se este PUT fosse pelo cliente do TestServer, o teste
    /// nunca sairia do processo e não provaria que a assinatura funciona.
    /// </remarks>
    private static async Task<HttpResponseMessage> SubirAsync(UploadTicketResponse bilhete, byte[] conteudo)
    {
        using var http = new HttpClient();
        using var corpo = new ByteArrayContent(conteudo);

        // O Content-Type faz parte da assinatura. Mandar outro faz o storage responder 403.
        corpo.Headers.ContentType = new MediaTypeHeaderValue(bilhete.ContentType);

        return await http.PutAsync(new Uri(bilhete.UploadUrl), corpo);
    }

    private static async Task<AttachmentResponse> ConfirmarAsync(HttpClient cliente, Guid anexo)
    {
        var resposta = await cliente.PostAsync(
            new Uri($"/api/attachments/{anexo}/complete", UriKind.Relative), null);

        resposta.EnsureSuccessStatusCode();

        return (await resposta.Content.ReadFromJsonAsync<AttachmentResponse>())!;
    }
}
