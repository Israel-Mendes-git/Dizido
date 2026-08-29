using Dizido.Api.Auth;
using Dizido.Api.Conversations;
using Dizido.Api.Realtime;
using Dizido.Contracts.Conversations;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

internal static class ConversationEndpoints
{
    public static RouteGroupBuilder MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations").WithTags("Conversations");

        group.MapGet("/", async (
            ICurrentUser currentUser,
            DizidoDbContext db,
            ConversationPresenter presenter,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            // Ordenado por LastMessageAt: é para isso que aquele campo desnormalizado existe.
            // Sem ele, precisaríamos varrer as mensagens de cada conversa para achar a mais nova.
            var ids = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.UserId == me && m.LeftAt == null)
                .Select(m => m.ConversationId)
                .ToListAsync(ct);

            var conversations = await db.Conversations
                .AsNoTracking()
                .Include(c => c.Members)
                .Where(c => ids.Contains(c.Id))
                .OrderByDescending(c => c.LastMessageAt)
                .ToListAsync(ct);

            return Results.Ok(await presenter.ApresentarAsync(conversations, ct));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ICurrentUser currentUser,
            DizidoDbContext db,
            ConversationPresenter presenter,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var conversation = await db.Conversations
                .AsNoTracking()
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (conversation is null)
            {
                return Results.NotFound();
            }

            // 404 em vez de 403 de propósito: responder 403 confirmaria a existência da
            // conversa para quem não participa dela. Não vazar o que não é da sua conta
            // vale mais do que a precisão semântica do código de status.
            if (!conversation.IsActiveMember(me))
            {
                return Results.NotFound();
            }

            var responses = await presenter.ApresentarAsync([conversation], ct);
            return Results.Ok(responses[0]);
        });

        group.MapPost("/direct", async (
            CreateDirectRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            ConversationPresenter presenter,
            TimeProvider clock,
            IConversationNotifier notifier,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var otherExists = await db.Profiles.AnyAsync(u => u.Id == request.OtherUserId, ct);
            if (!otherExists)
            {
                return Results.NotFound(new { message = "Usuário destinatário não existe." });
            }

            // Um privado entre duas pessoas já existente deve ser reaproveitado, não duplicado.
            var existing = await db.Conversations
                .Include(c => c.Members)
                .Where(c => c.Type == ConversationType.Direct)
                .Where(c => c.Members.Any(m => m.UserId == me)
                         && c.Members.Any(m => m.UserId == request.OtherUserId))
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                var found = await presenter.ApresentarAsync([existing], ct);
                return Results.Ok(found[0]);
            }

            var conversation = Conversation.CreateDirect(me, request.OtherUserId, clock.GetUtcNow());

            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);

            var created = await presenter.ApresentarAsync([conversation], ct);

            // Inscreve as conexões já abertas dos dois no grupo desta conversa nova.
            // Sem isto, quem estivesse com o app aberto só a veria ao recarregar a página.
            await notifier.ConversationCreatedAsync(created[0], [me, request.OtherUserId]);

            return Results.Created($"/api/conversations/{conversation.Id}", created[0]);
        });

        group.MapPost("/groups", async (
            CreateGroupRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            ConversationPresenter presenter,
            TimeProvider clock,
            IConversationNotifier notifier,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var conversation = Conversation.CreateGroup(request.Title, me, clock.GetUtcNow());

            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);

            var created = await presenter.ApresentarAsync([conversation], ct);

            await notifier.ConversationCreatedAsync(created[0], [me]);

            return Results.Created($"/api/conversations/{conversation.Id}", created[0]);
        });

        return group;
    }
}
