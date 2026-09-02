using Dizido.Contracts.Reactions;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Reactions;

/// <summary>
/// Monta as reações de um lote de mensagens.
/// </summary>
/// <remarks>
/// <para>
/// Uma classe à parte, e não um método privado dentro de <c>MessageEndpoints</c>, porque três
/// lugares precisam da mesma coisa: o histórico, a sincronização e o próprio endpoint de
/// reagir. É a mesma lição que produziu o <c>ConversationPresenter</c> — a terceira cópia de
/// uma montagem de resposta é onde as três começam a divergir.
/// </para>
/// <para>
/// Estática porque não depende de nada além do banco que recebe. Os outros dois apresentadores
/// são serviços registrados no contêiner por carregarem dependências próprias (o assinador de
/// URLs, a presença); este não tem nenhuma.
/// </para>
/// </remarks>
internal static class ReactionPresenter
{
    /// <summary>
    /// As reações destas mensagens, agrupadas por emoji.
    /// </summary>
    /// <remarks>
    /// Uma consulta para a página inteira. Cinquenta mensagens virariam cinquenta idas ao banco
    /// se cada balão buscasse as próprias reações — o mesmo N+1 que os anexos e as citações
    /// evitam do mesmo jeito.
    /// <para>
    /// O agrupamento acontece em memória, e não com <c>GROUP BY</c> no banco, porque o que
    /// devolvemos é a lista de <b>quem</b> reagiu, não uma contagem. Uma consulta agregada
    /// teria que trazer as linhas de qualquer forma.
    /// </para>
    /// </remarks>
    public static async Task<Dictionary<Guid, List<ReactionSummary>>> DeMensagensAsync(
        DizidoDbContext db,
        IReadOnlyList<Guid> messageIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0)
        {
            return [];
        }

        var reacoes = await db.Reactions
            .AsNoTracking()
            .Where(r => messageIds.Contains(r.MessageId))

            // Pelo instante, para que a ordem dos emojis num balão seja a ordem em que
            // apareceram. Sem isto o banco devolve na ordem que lhe convier, e os emojis
            // dançariam de posição a cada recarga da página.
            .OrderBy(r => r.ReactedAt)
            .Select(r => new { r.MessageId, r.Emoji, r.UserId })
            .ToListAsync(ct);

        return reacoes
            .GroupBy(r => r.MessageId)
            .ToDictionary(
                porMensagem => porMensagem.Key,
                porMensagem => porMensagem
                    .GroupBy(r => r.Emoji, StringComparer.Ordinal)
                    .Select(porEmoji => new ReactionSummary(
                        porEmoji.Key,
                        [.. porEmoji.Select(r => r.UserId)]))
                    .ToList());
    }

    /// <summary>As reações de uma única mensagem — o que os endpoints de reagir devolvem.</summary>
    public static async Task<IReadOnlyList<ReactionSummary>> DeUmaMensagemAsync(
        DizidoDbContext db, Guid messageId, CancellationToken ct)
    {
        var todas = await DeMensagensAsync(db, [messageId], ct);

        return todas.GetValueOrDefault(messageId, []);
    }
}
