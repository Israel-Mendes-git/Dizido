using Dizido.Api.Auth;
using Dizido.Api.Realtime;
using Dizido.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;
using Dizido.Contracts.Messages;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

internal static class MessageEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    public static RouteGroupBuilder MapMessageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations/{conversationId:guid}/messages")
            .WithTags("Messages");

        group.MapPost("/", async (
            Guid conversationId,
            SendMessageRequest request,
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

            // Deduplicação: o cliente pode estar reenviando algo que já chegou, mas cuja
            // resposta se perdeu. Devolver a mensagem existente com 200 (e não criar outra)
            // é o que torna o retry seguro. O índice único em (SenderId, ClientMessageId)
            // é a rede de segurança caso duas requisições cheguem simultaneamente.
            var duplicate = await db.Messages
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.SenderId == me && m.ClientMessageId == request.ClientMessageId, ct);

            if (duplicate is not null)
            {
                return Results.Ok(await ToResponseAsync(duplicate, db, ct));
            }

            var conversation = await db.Conversations
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conversation is null || !conversation.IsActiveMember(me))
            {
                return Results.NotFound();
            }

            // Toda a validação (membro ativo, corpo não vazio, tamanho) mora no domínio.
            // Se algo estiver errado, sai DomainException e o DomainExceptionHandler
            // converte em 400 — este endpoint não tem um único try/catch.
            var message = conversation.PostMessage(
                me,
                request.Body,
                request.ClientMessageId,
                clock.GetUtcNow(),
                request.ReplyToMessageId);

            db.Messages.Add(message);

            // Uma única transação grava a mensagem E o LastMessageAt da conversa, que o
            // PostMessage adiantou. Ou os dois acontecem, ou nenhum: a lista de conversas
            // nunca mostra "agora mesmo" para uma conversa cuja mensagem não foi gravada.
            await db.SaveChangesAsync(ct);

            var response = await ToResponseAsync(message, db, ct);

            // Notifica todo mundo na conversa, inclusive quem enviou.
            //
            // Incluir o próprio remetente é deliberado: o cliente já mostrou a mensagem
            // localmente (UI otimista) e usa este evento para confirmar — trocando o item
            // provisório pelo definitivo, casando o ClientMessageId. Um único caminho de
            // atualização para todos os clientes é mais simples do que dois.
            //
            // A notificação vem DEPOIS do SaveChanges. Se viesse antes e a gravação falhasse,
            // os outros veriam uma mensagem que não existe.
            await hub.Clients.Group(ChatHub.GroupName(conversationId)).MessageReceived(response);

            return Results.Created($"/api/conversations/{conversationId}/messages/{message.Id}", response);
        });

        group.MapGet("/", async (
            Guid conversationId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            CancellationToken ct,
            Guid? before = null,
            int? limit = null) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var isMember = await db.ConversationMembers.AnyAsync(
                m => m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null, ct);

            if (!isMember)
            {
                return Results.NotFound();
            }

            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

            // Pedimos take+1 para descobrir se existe página seguinte sem um COUNT extra:
            // se voltou um a mais do que cabe na página, há mais.
            var messages = await FetchPageAsync(db, conversationId, before, take + 1, ct);

            var hasMore = messages.Count > take;
            if (hasMore)
            {
                messages.RemoveAt(messages.Count - 1);
            }

            var senderIds = messages.Select(m => m.SenderId).Distinct().ToList();
            var names = await db.Profiles
                .AsNoTracking()
                .Where(u => senderIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

            var items = messages.Select(m => ToResponse(m, names.GetValueOrDefault(m.SenderId, "(desconhecido)"))).ToList();

            return Results.Ok(new MessagePage(items, hasMore ? messages[^1].Id : null));
        });

        return group;
    }

    /// <summary>
    /// Busca uma página de mensagens usando keyset pagination sobre o Id (UUIDv7).
    /// </summary>
    /// <remarks>
    /// Aqui usamos SQL em vez de LINQ por um motivo concreto: <see cref="Guid"/> não define
    /// os operadores &lt; e &gt; em C#, então <c>m.Id &lt; cursor</c> nem compila. O Postgres,
    /// por outro lado, compara o tipo <c>uuid</c> nativamente e na ordem correta dos bytes —
    /// que, com UUIDv7, é a ordem cronológica.
    /// <para>
    /// A interpolação aqui é segura: <c>FromSql</c> com string interpolada transforma cada
    /// valor em parâmetro da consulta, não em texto concatenado. Não há injeção de SQL.
    /// Concatenar com <c>+</c> ou <c>$"..."</c> passado como string comum, sim, seria falha grave.
    /// </para>
    /// </remarks>
    private static Task<List<Message>> FetchPageAsync(
        DizidoDbContext db,
        Guid conversationId,
        Guid? before,
        int take,
        CancellationToken ct) =>
        before is null
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
                     WHERE "ConversationId" = {conversationId} AND "Id" < {before}
                     ORDER BY "Id" DESC
                     LIMIT {take}
                     """)
                .AsNoTracking().ToListAsync(ct);

    private static async Task<MessageResponse> ToResponseAsync(
        Message message,
        DizidoDbContext db,
        CancellationToken ct)
    {
        var name = await db.Profiles
            .AsNoTracking()
            .Where(u => u.Id == message.SenderId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);

        return ToResponse(message, name ?? "(desconhecido)");
    }

    private static MessageResponse ToResponse(Message m, string senderDisplayName, string? targetName = null) =>
        new(m.Id, m.ConversationId, m.SenderId, senderDisplayName, m.Body, m.ClientMessageId,
            m.ReplyToMessageId, m.SentAt, m.EditedAt, m.IsDeleted,
            m.Kind.ToString(), m.SystemEvent?.ToString(), m.SystemTargetId, targetName);
}
