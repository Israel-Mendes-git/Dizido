using Dizido.Api.Attachments;
using Dizido.Api.Auth;
using Dizido.Contracts.Messages;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

/// <summary>
/// Busca no histórico de mensagens.
/// </summary>
/// <remarks>
/// <para>
/// Esta é a funcionalidade que a decisão de <b>não</b> usar criptografia ponta a ponta comprou.
/// Com E2E, o servidor guardaria bytes ilegíveis e a busca teria de acontecer no dispositivo,
/// sobre o pedaço do histórico que ele tivesse baixado. Aqui o Postgres indexa tudo e responde
/// em milissegundos sobre anos de conversa.
/// </para>
/// <para>
/// O outro lado dessa moeda, registrado na seção 2.1 do PLANO: o servidor consegue ler as
/// mensagens. Num app hospedado por quem o usa, é um preço aceitável — e é o preço que já
/// estava pago desde que a decisão foi tomada.
/// </para>
/// </remarks>
internal static class SearchEndpoints
{
    private const int DefaultPageSize = 30;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Abaixo disto a busca devolve vazio em vez de resultado.
    /// </summary>
    /// <remarks>
    /// Uma letra casaria com quase tudo, produzindo uma lista inútil e uma consulta cara.
    /// </remarks>
    private const int MinimoDeCaracteres = 2;

    public static RouteGroupBuilder MapSearchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/search").WithTags("Search");

        group.MapGet("/", async (
            ICurrentUser currentUser,
            DizidoDbContext db,
            AttachmentPresenter presenter,
            CancellationToken ct,
            string? q = null,
            Guid? conversationId = null,
            int? limite = null) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var termo = (q ?? string.Empty).Trim();

            if (termo.Length < MinimoDeCaracteres)
            {
                return Results.Ok(new MessagePage([], null));
            }

            // As conversas de que ESTA pessoa participa. É o cerne da autorização da busca:
            // sem esta lista, procurar por uma palavra devolveria mensagens de conversas
            // alheias — o vazamento mais fácil de escrever sem perceber num recurso de busca.
            var minhas = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.UserId == me && m.LeftAt == null)
                .Select(m => m.ConversationId)
                .ToListAsync(ct);

            if (conversationId is { } uma)
            {
                // Buscar dentro de uma conversa específica. Se não for minha, o resultado é
                // vazio — e não um erro, que confirmaria que a conversa existe.
                minhas = minhas.Where(id => id == uma).ToList();
            }

            if (minhas.Count == 0)
            {
                return Results.Ok(new MessagePage([], null));
            }

            var take = Math.Clamp(limite ?? DefaultPageSize, 1, MaxPageSize);

            var encontradas = await BuscarAsync(db, minhas, termo, take, ct);

            var nomes = await db.Profiles
                .AsNoTracking()
                .Where(u => encontradas.Select(m => m.SenderId).Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            var itens = encontradas
                .Select(m => new MessageResponse(
                    m.Id, m.ConversationId, m.SenderId,
                    nomes.GetValueOrDefault(m.SenderId, "(desconhecido)"),
                    m.Body, m.ClientMessageId, m.ReplyToMessageId,
                    m.SentAt, m.EditedAt, m.IsDeleted, m.Kind.ToString()))
                .ToList();

            return Results.Ok(new MessagePage(itens, null));
        });

        return group;
    }

    /// <summary>
    /// A consulta de busca propriamente dita.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>plainto_tsquery</c> em vez de <c>to_tsquery</c>: o primeiro aceita texto digitado por
    /// gente, com espaços e pontuação, e trata tudo como "todas estas palavras". O segundo
    /// espera uma expressão com operadores e <b>lança erro</b> diante de um apóstrofo perdido —
    /// ou seja, transformaria a digitação de um usuário comum em erro 500.
    /// </para>
    /// <para>
    /// Ordenado por Id (que é UUIDv7, logo cronológico) e não por relevância: numa conversa,
    /// "a mais recente que menciona isso" é quase sempre o que se procura, e ts_rank custaria
    /// caro para responder outra pergunta.
    /// </para>
    /// <para>
    /// Só mensagens de gente, não apagadas: um aviso de sistema não é conteúdo que alguém
    /// procure, e uma mensagem apagada tem o corpo vazio de qualquer forma.
    /// </para>
    /// </remarks>
    private static Task<List<Message>> BuscarAsync(
        DizidoDbContext db,
        List<Guid> conversas,
        string termo,
        int take,
        CancellationToken ct) =>
        db.Messages.FromSql(
                $"""
                 SELECT * FROM messages
                 WHERE "ConversationId" = ANY({conversas})
                   AND "DeletedAt" IS NULL
                   AND "Kind" = 1
                   AND busca @@ plainto_tsquery('portuguese', {termo})
                 ORDER BY "Id" DESC
                 LIMIT {take}
                 """)
            .AsNoTracking()
            .ToListAsync(ct);
}
