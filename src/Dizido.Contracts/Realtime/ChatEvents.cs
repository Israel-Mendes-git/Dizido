namespace Dizido.Contracts.Realtime;

/// <summary>Alguém começou (ou parou) de digitar numa conversa.</summary>
/// <remarks>
/// Evento efêmero: não é gravado em lugar nenhum. Se o destinatário estiver offline, ele
/// simplesmente não recebe — e não faz falta.
/// </remarks>
public sealed record TypingEvent(Guid ConversationId, Guid UserId, string DisplayName, bool IsTyping);

/// <summary>Um usuário ficou online ou offline.</summary>
public sealed record PresenceEvent(Guid UserId, bool IsOnline, DateTimeOffset At);

/// <summary>Um membro avançou sua marca d'água de leitura.</summary>
public sealed record ReadReceiptEvent(Guid ConversationId, Guid UserId, Guid LastReadMessageId);

/// <summary>Uma mensagem foi apagada.</summary>
public sealed record MessageDeletedEvent(Guid ConversationId, Guid MessageId, DateTimeOffset At);

/// <summary>Alguém reagiu a uma mensagem, ou desfez a própria reação.</summary>
/// <param name="Added"><c>true</c> se a reação foi posta, <c>false</c> se foi tirada.</param>
/// <remarks>
/// <para>
/// Uma <b>diferença</b>, e não a lista completa de reações da mensagem. Duas razões: o evento
/// vai para o grupo inteiro a cada clique, e mandar o conjunto todo faria o tamanho da mensagem
/// crescer com a popularidade do balão; e a diferença é exatamente o que o cliente precisa
/// aplicar. Aplicá-la é idempotente dos dois lados (entra num conjunto, sai de um conjunto),
/// então receber o mesmo evento duas vezes não estraga a contagem.
/// </para>
/// <para>
/// <b>O que isto não cobre:</b> reações feitas em mensagens antigas enquanto você estava
/// offline. A sincronização traz mensagens novas, não alterações nas que você já tinha na tela,
/// então esse número fica desatualizado até a conversa ser recarregada. É uma escolha, não um
/// esquecimento: consertar exigiria a sincronização carregar o estado de reação de todo o
/// histórico visível, e uma contagem de emoji temporariamente defasada não faz mal a ninguém.
/// </para>
/// </remarks>
public sealed record MessageReactionEvent(
    Guid ConversationId,
    Guid MessageId,
    string Emoji,
    Guid UserId,
    bool Added);
