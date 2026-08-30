using Dizido.Api.Auth;
using Dizido.Api.Realtime;
using Dizido.Contracts.Decisions;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Realtime;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

/// <summary>
/// Decisões registradas a partir de mensagens.
/// </summary>
/// <remarks>
/// Ver <see cref="Decision"/> para o problema que isto resolve e por que fixar mensagem — a
/// solução dos outros aplicativos — não resolve.
/// </remarks>
internal static class DecisionEndpoints
{
    private const int TrechoMaximo = 160;

    public static RouteGroupBuilder MapDecisionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations/{conversationId:guid}/decisions")
            .WithTags("Decisions");

        group.MapPost("/", async (
            Guid conversationId,
            RegisterDecisionRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            if (!await EhMembroAsync(db, conversationId, me, ct))
            {
                return Results.NotFound();
            }

            var mensagem = await db.Messages.AsNoTracking().FirstOrDefaultAsync(
                m => m.Id == request.MessageId && m.ConversationId == conversationId, ct);

            if (mensagem is null)
            {
                return Results.NotFound(new { message = "Mensagem não encontrada nesta conversa." });
            }

            var agora = clock.GetUtcNow();
            var (decisao, aviso) = Decision.Register(conversationId, mensagem, me, request.Summary, agora);

            // A revisão é aplicada ANTES de gravar, para que as duas mudanças entrem na mesma
            // transação: ou a decisão nova existe e a antiga aponta para ela, ou nada acontece.
            // Separado, um erro no meio deixaria a corrente quebrada.
            if (request.SupersedesDecisionId is { } anteriorId)
            {
                var anterior = await db.Decisions.FirstOrDefaultAsync(
                    d => d.Id == anteriorId && d.ConversationId == conversationId, ct);

                if (anterior is null)
                {
                    return Results.NotFound(new { message = "A decisão revista não existe nesta conversa." });
                }

                anterior.SupersededBy(decisao);
            }

            db.Decisions.Add(decisao);

            db.Messages.Add(aviso);

            await db.SaveChangesAsync(ct);

            var nome = await NomeAsync(db, me, ct);

            await hub.Clients.Group(ChatHub.GroupName(conversationId))
                .MessageReceived(ParaDto(aviso, nome));

            return Results.Ok(Apresentar(decisao, nome, mensagem, request.SupersedesDecisionId));
        });

        // O painel. Ordem cronológica inversa: a decisão mais recente é quase sempre a que
        // vale, e é a que alguém procura primeiro.
        group.MapGet("/", async (
            Guid conversationId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            CancellationToken ct,
            bool incluirRevistas = false) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            if (!await EhMembroAsync(db, conversationId, me, ct))
            {
                return Results.NotFound();
            }

            var consulta = db.Decisions
                .AsNoTracking()
                .Where(d => d.ConversationId == conversationId);

            if (!incluirRevistas)
            {
                // Por padrão só o que ainda vale. As revistas continuam ali, atrás de um
                // filtro, porque a corrente é o valor: "decidido em março, revisto em agosto".
                consulta = consulta.Where(d => d.SupersededByDecisionId == null);
            }

            var decisoes = await consulta
                .OrderByDescending(d => d.Id)
                .ToListAsync(ct);

            if (decisoes.Count == 0)
            {
                return Results.Ok(Array.Empty<DecisionResponse>());
            }

            // Nomes e mensagens de origem em lote — o mesmo cuidado com N+1 das outras telas.
            var autores = decisoes.Select(d => d.RegisteredById).Distinct().ToList();
            var nomes = await db.Profiles
                .AsNoTracking()
                .Where(u => autores.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            var idsDeMensagens = decisoes.Select(d => d.MessageId).ToList();
            var mensagens = await db.Messages
                .AsNoTracking()
                .Where(m => idsDeMensagens.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m, ct);

            // Quem revê quem: o inverso do SupersededByDecisionId, para o painel conseguir
            // mostrar "esta revê aquela" sem uma segunda consulta por item.
            var revisoes = decisoes
                .Where(d => d.SupersededByDecisionId is not null)
                .ToDictionary(d => d.SupersededByDecisionId!.Value, d => d.Id);

            return Results.Ok(decisoes.Select(d => Apresentar(
                d,
                nomes.GetValueOrDefault(d.RegisteredById, "(desconhecido)"),
                mensagens.GetValueOrDefault(d.MessageId),
                revisoes.GetValueOrDefault(d.Id) is var anterior && anterior != Guid.Empty ? anterior : null)));
        });

        // Desfazer o registro. Só quem registrou — não é moderação, é corrigir o próprio
        // engano de ter marcado a mensagem errada.
        group.MapDelete("/{decisionId:guid}", async (
            Guid conversationId,
            Guid decisionId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var decisao = await db.Decisions.FirstOrDefaultAsync(
                d => d.Id == decisionId && d.ConversationId == conversationId, ct);

            if (decisao is null || decisao.RegisteredById != me)
            {
                return Results.NotFound();
            }

            db.Decisions.Remove(decisao);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return group;
    }

    private static Task<bool> EhMembroAsync(
        DizidoDbContext db, Guid conversationId, Guid me, CancellationToken ct) =>
        db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null, ct);

    private static async Task<string> NomeAsync(DizidoDbContext db, Guid userId, CancellationToken ct) =>
        await db.Profiles.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "(desconhecido)";

    private static DecisionResponse Apresentar(
        Decision d, string nome, Message? origem, Guid? revê) =>
        new(d.Id,
            d.ConversationId,
            d.MessageId,
            d.Summary,
            d.RegisteredById,
            nome,
            d.RegisteredAt,
            Trecho(origem),
            d.SupersededByDecisionId,
            revê);

    /// <summary>Um pedaço da mensagem de origem, para dar contexto sem sair do painel.</summary>
    private static string Trecho(Message? mensagem)
    {
        if (mensagem is null)
        {
            return string.Empty;
        }

        if (mensagem.IsDeleted)
        {
            // A decisão continua valendo mesmo que a mensagem tenha sido apagada depois — é
            // por isso que o resumo é escrito à mão em vez de copiado do corpo.
            return "mensagem apagada";
        }

        var texto = mensagem.Body.Trim();

        if (texto.Length == 0)
        {
            return mensagem.HasAttachment ? "arquivo" : string.Empty;
        }

        return texto.Length <= TrechoMaximo ? texto : texto[..TrechoMaximo] + "…";
    }

    private static MessageResponse ParaDto(Message m, string nomeDoAutor) =>
        new(m.Id, m.ConversationId, m.SenderId, nomeDoAutor, m.Body, m.ClientMessageId,
            m.ReplyToMessageId, m.SentAt, m.EditedAt, m.IsDeleted,
            m.Kind.ToString(), m.SystemEvent?.ToString(), m.SystemTargetId, null);
}
