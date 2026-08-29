using Dizido.Api.Auth;
using Dizido.Api.Conversations;
using Dizido.Api.Realtime;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Sync;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

internal static class SyncEndpoints
{
    /// <summary>
    /// Teto de mensagens por conversa numa sincronização.
    /// </summary>
    /// <remarks>
    /// Sem limite, quem ficou uma semana offline num grupo movimentado receberia dezenas de
    /// milhares de mensagens numa única resposta — travando o navegador e ocupando o servidor.
    /// Quando o corte acontece, a conversa entra em <c>Truncated</c> e o cliente sabe que
    /// precisa buscar o resto pela paginação normal, em vez de achar que está em dia.
    /// </remarks>
    private const int MaxPorConversa = 200;

    public static RouteGroupBuilder MapSyncEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/sync").WithTags("Sync");

        group.MapPost("/", async (
            SyncRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            ConversationPresenter presenter,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            // As conversas vêm do banco, não da lista que o cliente mandou: enquanto ele
            // esteve offline, pode ter sido adicionado a grupos que ele nem sabe que existem.
            var minhasIds = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.UserId == me && m.LeftAt == null)
                .Select(m => m.ConversationId)
                .ToListAsync(ct);

            var conversas = await db.Conversations
                .AsNoTracking()
                .Include(c => c.Members)
                .Where(c => minhasIds.Contains(c.Id))
                .OrderByDescending(c => c.LastMessageAt)
                .ToListAsync(ct);

            // Só aceitamos cursores de conversas das quais o usuário realmente participa.
            // Sem este filtro, mandar um conversationId qualquer leria mensagens alheias.
            var cursores = request.Conversations
                .Where(c => minhasIds.Contains(c.ConversationId))
                .ToDictionary(c => c.ConversationId, c => c.LastMessageId);

            var mensagens = new List<Message>();
            var truncadas = new List<Guid>();

            foreach (var id in minhasIds)
            {
                cursores.TryGetValue(id, out var depoisDe);

                var novas = await BuscarDepoisAsync(db, id, depoisDe, MaxPorConversa + 1, ct);

                if (novas.Count > MaxPorConversa)
                {
                    novas.RemoveAt(novas.Count - 1);
                    truncadas.Add(id);
                }

                mensagens.AddRange(novas);
            }

            // Remetentes E alvos de aviso de sistema. O alvo de "Fulano removeu Beltrano" não
            // é remetente de nada, e sem ele o nome sairia como "(desconhecido)" — que é
            // exatamente o tipo de detalhe que só aparece depois, na tela de alguém.
            var pessoas = mensagens
                .Select(m => m.SenderId)
                .Concat(mensagens.Where(m => m.SystemTargetId is not null).Select(m => m.SystemTargetId!.Value))
                .Distinct()
                .ToList();

            var nomes = await db.Profiles
                .AsNoTracking()
                .Where(u => pessoas.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            return Results.Ok(new SyncResponse(
                await presenter.ApresentarAsync(conversas, ct),
                [.. mensagens.Select(m => new MessageResponse(
                    m.Id, m.ConversationId, m.SenderId,
                    nomes.GetValueOrDefault(m.SenderId, "(desconhecido)"),
                    m.Body, m.ClientMessageId, m.ReplyToMessageId,
                    m.SentAt, m.EditedAt, m.IsDeleted,
                    m.Kind.ToString(), m.SystemEvent?.ToString(), m.SystemTargetId,
                    m.SystemTargetId is { } alvo ? nomes.GetValueOrDefault(alvo) : null))],
                truncadas));
        });

        return group;
    }

    /// <summary>Mensagens gravadas DEPOIS do cursor, em ordem cronológica.</summary>
    /// <remarks>
    /// Ao contrário da paginação do histórico (que anda para trás, com <c>Id &lt; cursor</c>),
    /// aqui andamos para frente: <c>Id &gt; cursor</c>, ascendente. O mesmo índice
    /// <c>(ConversationId, Id)</c> serve para os dois sentidos.
    /// </remarks>
    private static Task<List<Message>> BuscarDepoisAsync(
        DizidoDbContext db, Guid conversationId, Guid? depoisDe, int take, CancellationToken ct) =>
        depoisDe is null
            ? db.Messages.FromSql(
                    $"""
                     SELECT * FROM messages
                     WHERE "ConversationId" = {conversationId}
                     ORDER BY "Id" DESC
                     LIMIT {take}
                     """)
                .AsNoTracking().ToListAsync(ct)
            : db.Messages.FromSql(
                    $"""
                     SELECT * FROM messages
                     WHERE "ConversationId" = {conversationId} AND "Id" > {depoisDe}
                     ORDER BY "Id" ASC
                     LIMIT {take}
                     """)
                .AsNoTracking().ToListAsync(ct);
}
