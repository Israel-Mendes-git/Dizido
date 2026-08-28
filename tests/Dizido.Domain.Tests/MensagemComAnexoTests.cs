using Dizido.Domain;
using Dizido.Domain.Entities;

namespace Dizido.Domain.Tests;

/// <summary>
/// As regras de quando um arquivo já enviado pode virar mensagem.
/// </summary>
public sealed class MensagemComAnexoTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bruno = Guid.CreateVersion7();

    private static Attachment AnexoPronto(Guid conversa, Guid autor)
    {
        var anexo = Attachment.Request(conversa, autor, "foto.png", "image/png", 1024, Now);
        anexo.Confirm("image/png", 1024, Now, width: 100, height: 80);

        return anexo;
    }

    [Fact]
    public void FotoSemLegendaEhMensagemValida()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var anexo = AnexoPronto(conversa.Id, Alice);

        var mensagem = conversa.PostMessage(Alice, string.Empty, Guid.CreateVersion7(), Now, attachment: anexo);

        Assert.True(mensagem.HasAttachment);
        Assert.Equal(anexo.Id, mensagem.AttachmentId);
        Assert.Equal(string.Empty, mensagem.Body);
    }

    [Fact]
    public void MensagemSemAnexoContinuaPrecisandoDeTexto()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);

        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, "   ", Guid.CreateVersion7(), Now));
    }

    /// <summary>
    /// O caso que mais importa: republicar numa conversa sua um arquivo de outra conversa.
    /// </summary>
    /// <remarks>
    /// Se passasse, o download autorizaria — ele confere a conversa <b>do anexo</b>, que
    /// continuaria sendo a de origem, e não a conversa onde a mensagem apareceu.
    /// </remarks>
    [Fact]
    public void AnexoDeOutraConversaEhRecusado()
    {
        var outra = Conversation.CreateDirect(Alice, Bruno, Now);
        var aqui = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);

        var anexo = AnexoPronto(outra.Id, Alice);

        var erro = Assert.Throws<DomainException>(
            () => aqui.PostMessage(Alice, "olha", Guid.CreateVersion7(), Now, attachment: anexo));

        Assert.Contains("outra conversa", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnexoEnviadoPorOutraPessoaEhRecusado()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var anexo = AnexoPronto(conversa.Id, Bruno);

        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, "olha", Guid.CreateVersion7(), Now, attachment: anexo));
    }

    [Fact]
    public void AnexoAindaNaoConfirmadoEhRecusado()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var pendente = Attachment.Request(conversa.Id, Alice, "foto.png", "image/png", 1024, Now);

        var erro = Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, "olha", Guid.CreateVersion7(), Now, attachment: pendente));

        Assert.Contains("ainda não terminou", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NaoMembroNaoAnexaNemComArquivoValido()
    {
        var conversa = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var anexo = AnexoPronto(conversa.Id, Bruno);

        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Bruno, "entrei", Guid.CreateVersion7(), Now, attachment: anexo));
    }

    [Fact]
    public void LegendaDeFotoPodeSerApagadaNaEdicao()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var anexo = AnexoPronto(conversa.Id, Alice);

        var mensagem = conversa.PostMessage(Alice, "legenda", Guid.CreateVersion7(), Now, attachment: anexo);

        mensagem.Edit(Alice, string.Empty, Now);

        Assert.Equal(string.Empty, mensagem.Body);
        Assert.True(mensagem.IsEdited);
    }

    [Fact]
    public void MensagemDeTextoNaoPodeSerEsvaziadaNaEdicao()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);
        var mensagem = conversa.PostMessage(Alice, "texto", Guid.CreateVersion7(), Now);

        Assert.Throws<DomainException>(() => mensagem.Edit(Alice, "  ", Now));
    }
}
