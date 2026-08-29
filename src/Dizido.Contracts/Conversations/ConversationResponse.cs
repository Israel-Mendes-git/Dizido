namespace Dizido.Contracts.Conversations;

public sealed record ConversationResponse(
    Guid Id,
    string Type,
    string? Title,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMessageAt,
    IReadOnlyList<ConversationMemberResponse> Members,

    /// <summary>Quantas mensagens chegaram depois da marca de leitura de quem pediu.</summary>
    /// <remarks>
    /// <para>
    /// Calculado por usuário, a partir do <c>LastReadMessageId</c> da linha dele. Não conta as
    /// próprias mensagens nem avisos de sistema: ninguém precisa ser avisado do que escreveu,
    /// e "Fulano entrou no grupo" não é algo a ler.
    /// </para>
    /// <para>
    /// Vem do servidor, e não é calculado no cliente, porque o cliente só tem em memória as
    /// mensagens que carregou — quem ficou uma semana fora veria "3 não lidas" quando são
    /// trezentas.
    /// </para>
    /// </remarks>
    int UnreadCount = 0);

public sealed record ConversationMemberResponse(
    Guid UserId,
    string DisplayName,
    string Role,
    Guid? LastReadMessageId,
    bool IsOnline,

    /// <summary>Até quando este membro silenciou a conversa. Nulo se não silenciou.</summary>
    /// <remarks>
    /// <para>
    /// Vem por membro, e não só para quem pediu, porque a resposta é a mesma para todo mundo —
    /// e é o que o cliente já tem em mãos ao montar a lista. O interessado procura a própria
    /// linha.
    /// </para>
    /// <para>
    /// Silenciar é preferência pessoal e não é segredo de ninguém: saber que um colega
    /// silenciou o grupo não dá acesso a nada. Se um dia incomodar, é só filtrar no servidor.
    /// </para>
    /// </remarks>
    DateTimeOffset? MutedUntil = null);

public sealed record CreateGroupRequest(string Title);

public sealed record CreateDirectRequest(Guid OtherUserId);
