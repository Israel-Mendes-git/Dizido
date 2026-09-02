namespace Dizido.Contracts.Reactions;

/// <summary>
/// Os emojis que o Dizido aceita como reação.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que uma lista fechada</b>, e não qualquer emoji do teclado. Três razões, em ordem
/// de importância: um seletor de seis botões se usa num toque, enquanto um teclado de emoji
/// inteiro exige procurar; a lista fechada limita a quantidade de reações distintas que um
/// balão pode acumular, então o rodapé nunca vira uma parede de figurinhas; e ela impede que
/// a coluna do banco receba coisas que ninguém previu.
/// </para>
/// <para>
/// <b>Por que aqui e não no domínio.</b> A interface precisa desta lista para desenhar o
/// seletor, e o cliente só enxerga <c>Dizido.Contracts</c> — <c>Dizido.Domain</c> é do servidor.
/// Duplicá-la nos dois lados garantiria o pior defeito possível para uma lista assim: um botão
/// que a tela oferece e o servidor recusa. O domínio guarda o que é invariante (ver
/// <c>Reaction.MaxEmojiLength</c>: forma de emoji, sem espaços, tamanho limitado); a paleta é
/// decisão de produto e muda sem migração nenhuma.
/// </para>
/// </remarks>
public static class ReactionPalette
{
    /// <summary>
    /// A paleta, na ordem em que o seletor a mostra.
    /// </summary>
    /// <remarks>
    /// Escolhidos pelo que uma equipe pequena responde no dia a dia, não pelos mais populares
    /// da internet: concordar, avisar que fez, gostar, rir, comemorar e sinalizar "estou vendo
    /// isso". Cada um cobre uma resposta que hoje vira mensagem.
    /// </remarks>
    public static readonly IReadOnlyList<string> Emojis =
    [
        "👍",              // concordo, pode seguir
        "✅",              // feito
        "\u2764\uFE0F",   // ❤️ gostei. Escrito em código, e não como literal, porque o segundo
                           // ponto de código é INVISÍVEL: ele só pede a versão colorida do
                           // coração. Copiado e colado por aí, some sem aviso — e o que sobra
                           // é outro texto, que desenha um coração preto e não casa com nada.
        "😂",              // engraçado
        "🎉",              // comemorar
        "👀",              // estou olhando, é comigo
    ];

    /// <summary>
    /// O emoji está na paleta?
    /// </summary>
    /// <remarks>
    /// Comparação <b>ordinal</b>, byte a byte, de propósito. Uma comparação "esperta", que
    /// ignorasse o seletor invisível do coração, deixaria as duas versões entrarem no banco —
    /// e o mesmo coração apareceria como duas reações separadas no mesmo balão, cada uma com
    /// sua contagem.
    /// </remarks>
    public static bool Contem(string? emoji) =>
        emoji is not null && Emojis.Contains(emoji, StringComparer.Ordinal);
}

/// <summary>Reage a uma mensagem com um emoji da paleta.</summary>
public sealed record ReactRequest(string Emoji);

/// <summary>
/// Quem reagiu a uma mensagem com um determinado emoji.
/// </summary>
/// <param name="UserIds">
/// Os identificadores, e não uma contagem pronta. Dois motivos: a interface precisa saber se
/// <i>você</i> está na lista para destacar a sua reação e para o clique saber se adiciona ou
/// remove; e num grupo pequeno "quem reagiu" é a informação que importa — a contagem sai
/// disto, o contrário não.
/// </param>
/// <remarks>
/// Repare que não existe um campo <c>Minha</c> aqui, e isso não é esquecimento: este DTO viaja
/// dentro de <c>MessageResponse</c>, que o servidor emite <b>uma vez só</b> para o grupo inteiro
/// pelo SignalR. Um campo calculado do ponto de vista de quem pergunta estaria certo para uma
/// pessoa e errado para todas as outras.
/// </remarks>
public sealed record ReactionSummary(string Emoji, IReadOnlyList<Guid> UserIds);
