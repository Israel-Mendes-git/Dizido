using Dizido.Domain;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;

namespace Dizido.Domain.Tests;

public sealed class AttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Conversa = Guid.CreateVersion7();
    private static readonly Guid Alice = Guid.CreateVersion7();

    private static Attachment Pedir(
        string nome = "foto.png",
        string tipo = "image/png",
        long tamanho = 1024,
        Guid? conversa = null,
        Guid? autor = null) =>
        Attachment.Request(conversa ?? Conversa, autor ?? Alice, nome, tipo, tamanho, Now);

    [Fact]
    public void FormatoDeImagemConhecidoEntraComoImagem()
    {
        var anexo = Pedir(tipo: "image/jpeg");

        Assert.Equal(AttachmentKind.Image, anexo.Kind);
        Assert.Equal("image/jpeg", anexo.ContentType);
        Assert.Equal(AttachmentStatus.Pending, anexo.Status);
        Assert.False(anexo.IsReady);
    }

    /// <summary>
    /// O que não está na lista de imagens vira arquivo comum — inclusive tipos que o navegador
    /// saberia renderizar, como SVG e HTML. É o comportamento seguro por omissão.
    /// </summary>
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("")]
    public void QualquerOutroTipoViraArquivoComum(string tipo)
    {
        var anexo = Pedir(nome: "coisa.bin", tipo: tipo);

        Assert.Equal(AttachmentKind.File, anexo.Kind);
        Assert.Equal("application/octet-stream", anexo.ContentType);
    }

    [Fact]
    public void ImagemTemLimiteMenorQueArquivo()
    {
        var vinteMegas = 20L * 1024 * 1024;

        // O mesmo tamanho: recusado como imagem, aceito como arquivo.
        Assert.Throws<DomainException>(() => Pedir(tipo: "image/png", tamanho: vinteMegas));

        var arquivo = Pedir(nome: "video.mp4", tipo: "video/mp4", tamanho: vinteMegas);
        Assert.Equal(AttachmentKind.File, arquivo.Kind);
    }

    [Fact]
    public void ArquivoAcimaDoLimiteEhRecusado()
    {
        var erro = Assert.Throws<DomainException>(
            () => Pedir(nome: "enorme.zip", tipo: "application/zip", tamanho: Attachment.MaxFileBytes + 1));

        Assert.Contains("50 MB", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArquivoVazioEhRecusado()
    {
        Assert.Throws<DomainException>(() => Pedir(tamanho: 0));
    }

    /// <summary>
    /// O caminho no storage é montado só com identificadores. Nome nenhum do usuário entra
    /// nele — nem sanitizado.
    /// </summary>
    [Fact]
    public void CaminhoNoStorageNaoUsaONomeEnviado()
    {
        var anexo = Pedir(nome: "../../etc/senha.png");

        Assert.Equal($"conversas/{Conversa:N}/{anexo.Id:N}", anexo.StorageKey);
        Assert.DoesNotContain("senha", anexo.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain("..", anexo.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public void NomeGuardadoPerdeSeparadoresDeCaminho()
    {
        var anexo = Pedir(nome: "../../etc/senha.png");

        Assert.Equal("....etcsenha.png", anexo.FileName);
    }

    [Fact]
    public void NomeComQuebraDeLinhaNaoPassa()
    {
        // Quebra de linha no nome viajaria no cabeçalho Content-Disposition do download.
        var anexo = Pedir(nome: "nota\r\nX-Coisa: injetada.txt");

        Assert.DoesNotContain('\r', anexo.FileName);
        Assert.DoesNotContain('\n', anexo.FileName);
    }

    [Fact]
    public void NomeSoComPontosEhRecusado()
    {
        Assert.Throws<DomainException>(() => Pedir(nome: ".."));
        Assert.Throws<DomainException>(() => Pedir(nome: "   "));
    }

    [Fact]
    public void ConfirmarTornaOAnexoUsavel()
    {
        var anexo = Pedir(tamanho: 1024);

        anexo.Confirm("image/png", 2048, Now, width: 800, height: 600, thumbnailKey: "thumb");

        Assert.True(anexo.IsReady);
        Assert.Equal(800, anexo.Width);
        Assert.Equal(600, anexo.Height);
        Assert.Equal("thumb", anexo.ThumbnailKey);
        Assert.Equal(Now, anexo.ReadyAt);
    }

    /// <summary>
    /// O tamanho que vale é o que o storage relata. O do pedido era só uma promessa, e nada
    /// obrigava o cliente a cumpri-la na hora de subir os bytes.
    /// </summary>
    [Fact]
    public void ConfirmarUsaOTamanhoRealENaoOPrometido()
    {
        var anexo = Pedir(tamanho: 10);

        anexo.Confirm("image/png", 4096, Now, width: 10, height: 10);

        Assert.Equal(4096, anexo.SizeBytes);
    }

    [Fact]
    public void ArquivoQueChegouMaiorQueOLimiteEhRecusadoNaConfirmacao()
    {
        var anexo = Pedir(tamanho: 1024);

        Assert.Throws<DomainException>(
            () => anexo.Confirm("image/png", Attachment.MaxImageBytes + 1, Now, width: 10, height: 10));

        Assert.False(anexo.IsReady);
    }

    /// <summary>
    /// Pediu para subir PNG, subiu outra coisa. É o caso que o "nunca confie no Content-Type"
    /// existe para pegar.
    /// </summary>
    [Fact]
    public void ImagemCujosBytesNaoSaoDeImagemEhRecusada()
    {
        var anexo = Pedir(tipo: "image/png");

        var erro = Assert.Throws<DomainException>(
            () => anexo.Confirm("text/html", 1024, Now, width: 1, height: 1));

        Assert.Contains("não é uma imagem", erro.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(anexo.IsReady);
    }

    [Fact]
    public void ImagemSemDimensoesEhRecusada()
    {
        var anexo = Pedir(tipo: "image/png");

        Assert.Throws<DomainException>(() => anexo.Confirm("image/png", 1024, Now));
    }

    [Fact]
    public void ArquivoComumNaoPrecisaDeDimensoes()
    {
        var anexo = Pedir(nome: "contrato.pdf", tipo: "application/pdf");

        anexo.Confirm("application/pdf", 5000, Now);

        Assert.True(anexo.IsReady);
        Assert.Null(anexo.Width);

        // O tipo continua octet-stream: arquivo comum vai para download, nunca para o
        // renderizador do navegador, e é assim que fica.
        Assert.Equal("application/octet-stream", anexo.ContentType);
    }

    [Fact]
    public void ConfirmarDuasVezesEhRecusado()
    {
        var anexo = Pedir(nome: "contrato.pdf", tipo: "application/pdf");

        anexo.Confirm("application/pdf", 5000, Now);

        Assert.Throws<DomainException>(() => anexo.Confirm("application/pdf", 5000, Now));
    }
}
