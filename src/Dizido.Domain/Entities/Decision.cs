namespace Dizido.Domain.Entities;

/// <summary>
/// Uma decisão registrada a partir de uma mensagem da conversa.
/// </summary>
/// <remarks>
/// <para>
/// <b>O problema que isto resolve.</b> Numa equipe pequena, as decisões se perdem no chat.
/// "Ficou combinado que o chefe usa o sistema de stamina antigo?" — e alguém rola três semanas
/// de conversa procurando. A decisão foi tomada, todo mundo concordou, e ninguém acha.
/// </para>
/// <para>
/// Fixar mensagem, que é a solução dos outros aplicativos, não resolve: vira um amontoado sem
/// ordem, sem autoria e — o que mais importa — <b>sem o porquê</b>. Aqui, registrar exige
/// escrever uma linha explicando, e é essa linha que ainda vai fazer sentido daqui a um ano.
/// </para>
/// <para>
/// <b>O que isto não é.</b> Não é gestão de tarefas. Não tem responsável, prazo nem estado de
/// andamento. Uma decisão é um fato registrado, não um trabalho a fazer — e no dia em que
/// ganhar um campo "responsável", vira um gerenciador de tarefas ruim.
/// </para>
/// </remarks>
public sealed class Decision
{
    public const int MaxSummaryLength = 280;

    private Decision() { }

    public Guid Id { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>A mensagem em que a decisão foi tomada.</summary>
    /// <remarks>
    /// O elo com a discussão é o que separa isto de um documento à parte: quem lê a decisão
    /// consegue voltar ao ponto exato em que ela apareceu e ver o que foi dito em volta.
    /// </remarks>
    public Guid MessageId { get; private set; }

    public Guid RegisteredById { get; private set; }

    /// <summary>O que ficou decidido e por quê, em uma linha.</summary>
    /// <remarks>
    /// Escrito por quem registra, e não copiado da mensagem. A mensagem original costuma ser
    /// o fim de uma discussão ("então fica assim mesmo") e não se explica sozinha meses depois.
    /// </remarks>
    public string Summary { get; private set; } = null!;

    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>A decisão que substituiu esta, se houver.</summary>
    /// <remarks>
    /// Decisão revista não é apagada: ela vira o começo de uma corrente. "Decidido em março,
    /// revisto em agosto, e o motivo de cada uma" é mais útil do que só a versão atual — é o
    /// que impede a equipe de refazer a mesma discussão por não lembrar por que mudou.
    /// </remarks>
    public Guid? SupersededByDecisionId { get; private set; }

    public bool IsActive => SupersededByDecisionId is null;

    /// <summary>
    /// Registra uma decisão a partir de uma mensagem.
    /// </summary>
    /// <param name="mensagem">A mensagem que a contém. Precisa ser desta conversa.</param>
    /// <param name="registeredById">Quem está registrando. Qualquer participante pode.</param>
    /// <remarks>
    /// Qualquer participante registra, não só administradores. Quem percebe que algo ficou
    /// decidido raramente é quem manda no grupo — e exigir cargo faria a maior parte das
    /// decisões nunca ser registrada.
    /// </remarks>
    /// <returns>
    /// A decisão e o aviso de sistema a ser gravado no fluxo — o mesmo par que
    /// <see cref="Conversation.AddMember"/> devolve, pela mesma razão: quem cria mensagem no
    /// Dizido é o domínio, nunca o endpoint.
    /// </returns>
    public static (Decision Decisao, Message Aviso) Register(
        Guid conversationId,
        Message mensagem,
        Guid registeredById,
        string summary,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mensagem);

        DomainException.Require(
            mensagem.ConversationId == conversationId,
            "A mensagem pertence a outra conversa.");

        DomainException.Require(
            !mensagem.IsSystem,
            "Avisos do sistema não registram decisão.");

        DomainException.Require(
            !mensagem.IsDeleted,
            "Uma mensagem apagada não registra decisão.");

        var texto = (summary ?? string.Empty).Trim();

        DomainException.Require(
            texto.Length > 0,
            "Escreva o que ficou decidido — é essa linha que vai fazer sentido daqui a um ano.");

        DomainException.Require(
            texto.Length <= MaxSummaryLength,
            $"O resumo da decisão não pode passar de {MaxSummaryLength} caracteres.");

        var decisao = new Decision
        {
            Id = Guid.CreateVersion7(now),
            ConversationId = conversationId,
            MessageId = mensagem.Id,
            RegisteredById = registeredById,
            Summary = texto,
            RegisteredAt = now,
        };

        // O aviso no fluxo é o que faz as outras pessoas saberem que algo ficou combinado sem
        // precisarem abrir o painel — mesmo papel de "Fulano entrou no grupo".
        var aviso = Message.CreateSystemForDecision(conversationId, registeredById, now, texto);

        return (decisao, aviso);
    }

    /// <summary>Marca esta decisão como substituída por outra.</summary>
    public void SupersededBy(Decision nova)
    {
        ArgumentNullException.ThrowIfNull(nova);

        DomainException.Require(
            nova.ConversationId == ConversationId,
            "Uma decisão só pode ser revista por outra da mesma conversa.");

        DomainException.Require(nova.Id != Id, "Uma decisão não revê a si mesma.");

        DomainException.Require(
            IsActive,
            "Esta decisão já foi revista. Reveja a mais recente da corrente.");

        SupersededByDecisionId = nova.Id;
    }
}
