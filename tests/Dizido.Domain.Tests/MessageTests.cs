using Dizido.Domain;
using Dizido.Domain.Entities;

namespace Dizido.Domain.Tests;

public sealed class MessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bruno = Guid.CreateVersion7();

    private static Conversation NovaConversa() => Conversation.CreateDirect(Alice, Bruno, Now);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void MensagemVaziaEhRejeitada(string corpo)
    {
        var conversa = NovaConversa();

        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, corpo, Guid.NewGuid(), Now));
    }

    [Fact]
    public void MensagemAcimaDoLimiteEhRejeitada()
    {
        var conversa = NovaConversa();
        var gigante = new string('a', Message.MaxBodyLength + 1);

        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, gigante, Guid.NewGuid(), Now));
    }

    [Fact]
    public void MensagemSemClientMessageIdEhRejeitada()
    {
        var conversa = NovaConversa();

        // Guid.Empty significa que o cliente não gerou o identificador de deduplicação.
        Assert.Throws<DomainException>(
            () => conversa.PostMessage(Alice, "oi", Guid.Empty, Now));
    }

    [Fact]
    public void SoOAutorEditaAPropriaMensagem()
    {
        var conversa = NovaConversa();
        var mensagem = conversa.PostMessage(Alice, "erro de digitaçao", Guid.NewGuid(), Now);

        Assert.Throws<DomainException>(() => mensagem.Edit(Bruno, "editado", Now));

        mensagem.Edit(Alice, "erro de digitação", Now.AddSeconds(5));

        Assert.Equal("erro de digitação", mensagem.Body);
        Assert.True(mensagem.IsEdited);
    }

    [Fact]
    public void ApagarEsvaziaOCorpoMasMantemALinha()
    {
        var conversa = NovaConversa();
        var mensagem = conversa.PostMessage(Alice, "vou apagar", Guid.NewGuid(), Now);

        mensagem.Delete(Alice, Now.AddSeconds(2));

        Assert.True(mensagem.IsDeleted);
        Assert.Equal(string.Empty, mensagem.Body);
        // O Id continua válido: respostas e marcas de leitura que apontam para ela não quebram.
        Assert.NotEqual(Guid.Empty, mensagem.Id);
    }

    [Fact]
    public void ApagarDuasVezesNaoEhErro()
    {
        var conversa = NovaConversa();
        var mensagem = conversa.PostMessage(Alice, "vou apagar", Guid.NewGuid(), Now);

        mensagem.Delete(Alice, Now.AddSeconds(2));
        var primeiroDeletedAt = mensagem.DeletedAt;

        mensagem.Delete(Alice, Now.AddSeconds(9)); // reenvio do mesmo comando

        Assert.Equal(primeiroDeletedAt, mensagem.DeletedAt);
    }

    [Fact]
    public void MensagemApagadaNaoPodeSerEditada()
    {
        var conversa = NovaConversa();
        var mensagem = conversa.PostMessage(Alice, "oi", Guid.NewGuid(), Now);
        mensagem.Delete(Alice, Now.AddSeconds(2));

        Assert.Throws<DomainException>(() => mensagem.Edit(Alice, "voltei", Now.AddSeconds(3)));
    }

    [Fact]
    public void ModeradorPodeApagarMensagemAlheia()
    {
        var conversa = NovaConversa();
        var mensagem = conversa.PostMessage(Alice, "spam", Guid.NewGuid(), Now);

        mensagem.Delete(Bruno, Now.AddSeconds(1), isModerator: true);

        Assert.True(mensagem.IsDeleted);
    }

    [Fact]
    public void IdsDeMensagemSaoOrdenaveisNoTempo()
    {
        var conversa = NovaConversa();

        var ids = Enumerable.Range(0, 50)
            .Select(i => conversa.PostMessage(Alice, $"mensagem {i}", Guid.NewGuid(), Now.AddSeconds(i)).Id)
            .ToList();

        // Esta é a propriedade que sustenta a paginação por cursor e a marca d'água de
        // leitura: ordenar por Id é ordenar por tempo de criação. Com UUIDv4 falharia.
        var ordenados = ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToList();

        Assert.Equal(ordenados, ids);
    }
}
