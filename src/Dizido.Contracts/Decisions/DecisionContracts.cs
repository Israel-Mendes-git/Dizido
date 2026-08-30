namespace Dizido.Contracts.Decisions;

/// <summary>Registra uma decisão a partir de uma mensagem da conversa.</summary>
/// <param name="Summary">
/// O que ficou decidido e por quê, em uma linha. Escrito por quem registra, e não copiado da
/// mensagem: o original costuma ser o fim de uma discussão ("então fica assim mesmo") e não se
/// explica sozinho meses depois.
/// </param>
/// <param name="SupersedesDecisionId">
/// A decisão que esta revê, quando é o caso. A anterior não é apagada — vira o começo de uma
/// corrente, e o motivo de cada mudança fica registrado.
/// </param>
public sealed record RegisterDecisionRequest(
    Guid MessageId,
    string Summary,
    Guid? SupersedesDecisionId = null);

/// <summary>Uma decisão como o painel a exibe.</summary>
public sealed record DecisionResponse(
    Guid Id,
    Guid ConversationId,
    Guid MessageId,
    string Summary,
    Guid RegisteredById,
    string RegisteredByName,
    DateTimeOffset RegisteredAt,

    /// <summary>Trecho da mensagem de origem, para dar contexto sem sair do painel.</summary>
    string MessageExcerpt,

    /// <summary>A decisão que substituiu esta, se já foi revista.</summary>
    Guid? SupersededByDecisionId = null,

    /// <summary>A decisão que esta reviu, se for uma revisão.</summary>
    Guid? SupersedesDecisionId = null);
