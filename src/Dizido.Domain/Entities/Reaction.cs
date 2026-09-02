namespace Dizido.Domain.Entities;

/// <summary>
/// A reação de uma pessoa a uma mensagem: um emoji, uma pessoa, uma mensagem.
/// </summary>
/// <remarks>
/// <para>
/// <b>O problema que isto resolve.</b> Num grupo de dez pessoas, combinar um horário gera
/// dez mensagens de "ok" que empurram a conversa para cima e não dizem nada de novo. A reação
/// é a mesma concordância ocupando zero linhas do fluxo — o valor não é decorativo.
/// </para>
/// <para>
/// <b>A chave é composta:</b> <c>(MessageId, UserId, Emoji)</c>. Sem <c>Id</c> próprio, porque
/// a identidade de uma reação <i>é</i> exatamente essa trinca — e assim "a mesma pessoa não
/// reage duas vezes com o mesmo emoji" deixa de ser uma regra que alguém precisa lembrar de
/// checar e vira uma impossibilidade do banco.
/// </para>
/// <para>
/// Repare no que <b>não</b> existe aqui: nem <c>ConversationId</c> (a mensagem já sabe de qual
/// conversa é), nem contador. Guardar a contagem seria desnormalizar sem necessidade — ao
/// contrário de <see cref="Conversation.LastMessageAt"/>, que existe porque a lista de conversas
/// seria cara sem ele, a contagem sai de graça no mesmo <c>SELECT</c> que já traz quem reagiu.
/// </para>
/// </remarks>
public sealed class Reaction
{
    /// <summary>
    /// O maior emoji que aceitamos, em unidades UTF-16.
    /// </summary>
    /// <remarks>
    /// Não é um número arbitrário: um emoji só parece um caractere. "👨‍👩‍👧‍👦" é uma sequência de
    /// quatro pessoas coladas por juntadores invisíveis (ZWJ) e ocupa 11 posições numa
    /// <c>string</c> do .NET; um polegar com tom de pele ocupa 4. Dezesseis dá folga para as
    /// sequências reais e ainda assim impede que a coluna receba uma frase.
    /// </remarks>
    public const int MaxEmojiLength = 16;

    private Reaction() { }

    public Guid MessageId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>O emoji, como texto.</summary>
    /// <remarks>
    /// Texto, e não um enum ou um código numérico. Um enum obrigaria uma migração de banco a
    /// cada emoji novo — e a lista do que a interface oferece é uma decisão de produto, que
    /// muda com muito mais frequência do que um esquema deveria mudar.
    /// </remarks>
    public string Emoji { get; private set; } = null!;

    public DateTimeOffset ReactedAt { get; private set; }

    /// <summary>
    /// Cria a reação de alguém a uma mensagem.
    /// </summary>
    /// <param name="mensagem">A mensagem reagida. Precisa estar viva e não ser aviso do sistema.</param>
    /// <param name="userId">Quem está reagindo. Qualquer participante da conversa pode.</param>
    /// <remarks>
    /// Quem confere se <paramref name="userId"/> participa da conversa é o endpoint, como em
    /// <see cref="Decision.Register"/>: a reação é uma entidade à parte, ligada por identificador,
    /// e carregar a conversa inteira só para responder "esta pessoa está aqui?" custaria uma
    /// consulta a mais em cada clique.
    /// </remarks>
    public static Reaction Create(Message mensagem, Guid userId, string emoji, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mensagem);

        // Um aviso do sistema não é fala de ninguém; reagir a "Fulano entrou no grupo" não tem
        // a quem responder. E uma mensagem apagada já não mostra conteúdo — a reação ficaria
        // pendurada num balão que diz "esta mensagem foi apagada".
        DomainException.Require(!mensagem.IsSystem, "Avisos do sistema não recebem reação.");
        DomainException.Require(!mensagem.IsDeleted, "Não dá para reagir a uma mensagem apagada.");

        return new Reaction
        {
            MessageId = mensagem.Id,
            UserId = userId,
            Emoji = ValidarEmoji(emoji),
            ReactedAt = now,
        };
    }

    /// <summary>
    /// Confere que o texto tem forma de emoji, e devolve a versão que vai para o banco.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O que se protege aqui é a <b>integridade da coluna</b>, não a lista de emojis que a
    /// interface oferece — essa vive em <c>Dizido.Contracts</c>, porque o cliente precisa dela
    /// para desenhar o seletor e o cliente só enxerga os contratos. A separação não é acidental:
    /// "que emojis existem no seletor" é decisão de produto e muda sem migração; "o que pode ser
    /// gravado nesta coluna" é invariante e mora aqui.
    /// </para>
    /// <para>
    /// Espaço é recusado depois da limpeza das pontas, e não só nas pontas: <c>"👍 👎"</c> são
    /// dois emojis, e aceitá-los como um valor só criaria uma reação que a interface não sabe
    /// desenhar e que nenhum outro clique consegue igualar para desfazer.
    /// </para>
    /// </remarks>
    private static string ValidarEmoji(string emoji)
    {
        var texto = (emoji ?? string.Empty).Trim();

        DomainException.Require(texto.Length > 0, "A reação precisa de um emoji.");

        DomainException.Require(
            texto.Length <= MaxEmojiLength,
            $"Uma reação não pode passar de {MaxEmojiLength} caracteres.");

        DomainException.Require(
            !texto.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)),
            "Uma reação é um emoji só, sem espaços.");

        return texto;
    }
}
