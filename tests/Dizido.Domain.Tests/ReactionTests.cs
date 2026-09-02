using Dizido.Contracts.Reactions;
using Dizido.Domain;
using Dizido.Domain.Entities;

namespace Dizido.Domain.Tests;

/// <summary>
/// As reações. O domínio guarda a <b>forma</b> do emoji; a paleta — que emojis existem — mora
/// em <c>Dizido.Contracts</c>, porque a interface precisa dela para desenhar o seletor.
/// </summary>
public sealed class ReactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Alice = Guid.CreateVersion7();
    private static readonly Guid Bruno = Guid.CreateVersion7();

    private const string Polegar = "👍";

    private static Message NovaMensagem()
    {
        var conversa = Conversation.CreateDirect(Alice, Bruno, Now);

        return conversa.PostMessage(Alice, "combinado?", Guid.NewGuid(), Now);
    }

    [Fact]
    public void ReagirGuardaQuemQuandoEQual()
    {
        var mensagem = NovaMensagem();

        var reacao = Reaction.Create(mensagem, Bruno, Polegar, Now);

        Assert.Equal(mensagem.Id, reacao.MessageId);
        Assert.Equal(Bruno, reacao.UserId);
        Assert.Equal(Polegar, reacao.Emoji);
        Assert.Equal(Now, reacao.ReactedAt);
    }

    /// <summary>Reagir à própria mensagem é permitido — é assim em todo aplicativo de mensagem.</summary>
    [Fact]
    public void DaParaReagirAPropriaMensagem()
    {
        var mensagem = NovaMensagem();

        var reacao = Reaction.Create(mensagem, Alice, Polegar, Now);

        Assert.Equal(Alice, reacao.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ReacaoVaziaEhRejeitada(string emoji)
    {
        var mensagem = NovaMensagem();

        Assert.Throws<DomainException>(() => Reaction.Create(mensagem, Bruno, emoji, Now));
    }

    /// <summary>
    /// O que impede a coluna de virar um campo de texto livre.
    /// </summary>
    [Fact]
    public void TextoLongoNoLugarDoEmojiEhRejeitado()
    {
        var mensagem = NovaMensagem();

        Assert.Throws<DomainException>(
            () => Reaction.Create(mensagem, Bruno, "concordo com tudo isso aí", Now));
    }

    /// <summary>
    /// Dois emojis colados seriam uma reação que a interface não sabe desenhar — e que
    /// nenhum clique conseguiria igualar depois para desfazer.
    /// </summary>
    [Fact]
    public void DoisEmojisSeparadosPorEspacoSaoRejeitados()
    {
        var mensagem = NovaMensagem();

        Assert.Throws<DomainException>(() => Reaction.Create(mensagem, Bruno, "👍 👎", Now));
    }

    [Fact]
    public void EspacoEmVoltaEhAparadoEmVezDeRecusado()
    {
        var mensagem = NovaMensagem();

        var reacao = Reaction.Create(mensagem, Bruno, $"  {Polegar} ", Now);

        // Aparar, e não recusar: gravar "👍 " criaria uma reação distinta de "👍", e o mesmo
        // polegar apareceria duas vezes no balão, cada um com sua contagem.
        Assert.Equal(Polegar, reacao.Emoji);
    }

    [Fact]
    public void NaoDaParaReagirAMensagemApagada()
    {
        var mensagem = NovaMensagem();
        mensagem.Delete(Alice, Now);

        Assert.Throws<DomainException>(() => Reaction.Create(mensagem, Bruno, Polegar, Now));
    }

    [Fact]
    public void NaoDaParaReagirAAvisoDoSistema()
    {
        var conversa = Conversation.CreateGroup("Rapadura Atômica", Alice, Now);
        var aviso = conversa.AddMember(Alice, Bruno, Now);

        Assert.Throws<DomainException>(() => Reaction.Create(aviso, Alice, Polegar, Now));
    }

    // ----- a paleta -----

    /// <summary>
    /// A paleta e o domínio precisam concordar: um emoji que a interface oferece e o domínio
    /// recusa seria um botão que nunca funciona.
    /// </summary>
    [Fact]
    public void TodaAPaletaPassaNaValidacaoDoDominio()
    {
        var mensagem = NovaMensagem();

        foreach (var emoji in ReactionPalette.Emojis)
        {
            var reacao = Reaction.Create(mensagem, Bruno, emoji, Now);

            Assert.Equal(emoji, reacao.Emoji);
        }
    }

    [Fact]
    public void APaletaNaoTemRepetidos()
    {
        Assert.Equal(
            ReactionPalette.Emojis.Count,
            ReactionPalette.Emojis.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// O coração é o caso que quebra sozinho: ele é o símbolo mais um seletor invisível que
    /// pede a versão colorida. Se algum editor ou ferramenta comer esse segundo ponto de
    /// código, a paleta passa a guardar outro texto — e todas as reações já gravadas no banco
    /// deixam de casar com o botão da tela, sem erro nenhum em lugar nenhum.
    /// </summary>
    [Fact]
    public void OCoracaoDaPaletaMantemOSeletorDeVariacao()
    {
        Assert.Contains("❤️", ReactionPalette.Emojis, StringComparer.Ordinal);
    }

    [Fact]
    public void ForaDaPaletaNaoPassa()
    {
        Assert.False(ReactionPalette.Contem("🍕"));
        Assert.False(ReactionPalette.Contem(""));
        Assert.False(ReactionPalette.Contem(null));

        // O coração SEM o seletor invisível: desenha outra coisa, e não é o que está na paleta.
        Assert.False(ReactionPalette.Contem("❤"));
    }
}
