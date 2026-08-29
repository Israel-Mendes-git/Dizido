using Dizido.Domain;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;

namespace Dizido.Domain.Tests;

public sealed class AvatarDeGrupoTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bruno = Guid.CreateVersion7();

    private static Attachment ImagemPronta(Guid conversa, Guid autor)
    {
        var anexo = Attachment.Request(conversa, autor, "capa.png", "image/png", 2048, Now);
        anexo.Confirm("image/png", 2048, Now, width: 400, height: 400);

        return anexo;
    }

    private static Attachment ArquivoPronto(Guid conversa, Guid autor)
    {
        var anexo = Attachment.Request(conversa, autor, "manual.pdf", "application/pdf", 2048, Now);
        anexo.Confirm("application/pdf", 2048, Now);

        return anexo;
    }

    [Fact]
    public void AdministradorDefineAImagemDoGrupo()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var imagem = ImagemPronta(grupo.Id, Alice);

        var aviso = grupo.SetAvatar(Alice, imagem, Now);

        Assert.Equal(imagem.Id, grupo.AvatarAttachmentId);

        // Trocar a cara do grupo é assunto de quem está nele, ao contrário de silenciar.
        Assert.Equal(SystemEventKind.AvatarChanged, aviso.SystemEvent);
    }

    [Fact]
    public void PassarNuloRemoveAImagem()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);

        grupo.SetAvatar(Alice, ImagemPronta(grupo.Id, Alice), Now);
        grupo.SetAvatar(Alice, null, Now);

        Assert.Null(grupo.AvatarAttachmentId);
    }

    [Fact]
    public void MembroComumNaoTrocaAImagem()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        grupo.AddMember(Alice, Bruno, Now);

        var imagem = ImagemPronta(grupo.Id, Bruno);

        var erro = Assert.Throws<DomainException>(() => grupo.SetAvatar(Bruno, imagem, Now));

        Assert.Contains("administrador", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConversaDiretaNaoTemImagemPropria()
    {
        var privado = Conversation.CreateDirect(Alice, Bruno, Now);
        var imagem = ImagemPronta(privado.Id, Alice);

        Assert.Throws<DomainException>(() => privado.SetAvatar(Alice, imagem, Now));
    }

    /// <summary>
    /// Um PDF de avatar não daria erro em lugar nenhum — só apareceria como quadrado quebrado
    /// para todo mundo, para sempre.
    /// </summary>
    [Fact]
    public void ArquivoQueNaoEhImagemNaoServeDeAvatar()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var pdf = ArquivoPronto(grupo.Id, Alice);

        var erro = Assert.Throws<DomainException>(() => grupo.SetAvatar(Alice, pdf, Now));

        Assert.Contains("imagem", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImagemDeOutraConversaEhRecusada()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var outra = Conversation.CreateGroup("Outro", Alice, Now);

        var deOutroLugar = ImagemPronta(outra.Id, Alice);

        var erro = Assert.Throws<DomainException>(() => grupo.SetAvatar(Alice, deOutroLugar, Now));

        Assert.Contains("outra conversa", erro.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImagemAindaNaoConfirmadaEhRecusada()
    {
        var grupo = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var pendente = Attachment.Request(grupo.Id, Alice, "capa.png", "image/png", 2048, Now);

        Assert.Throws<DomainException>(() => grupo.SetAvatar(Alice, pendente, Now));
    }
}
