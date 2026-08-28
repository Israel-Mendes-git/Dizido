using Dizido.Api.Auth;
using Dizido.Api.Realtime;
using Dizido.Contracts.Conversations;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Realtime;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

/// <summary>
/// Administração de grupos: renomear, entrar, sair, remover, promover, silenciar.
/// </summary>
/// <remarks>
/// Nenhum destes endpoints decide quem pode o quê — quem decide é a entidade
/// <see cref="Conversation"/>. Aqui só carregamos, chamamos o método, gravamos e notificamos.
/// É por isso que os testes de permissão rodam sem banco: a regra não está espalhada em
/// verificações de HTTP.
/// </remarks>
internal static class GroupEndpoints
{
    public static RouteGroupBuilder MapGroupEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/conversations/{id:guid}").WithTags("Groups");

        group.MapPatch("/title", async (
            Guid id,
            RenameGroupRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            IPresenceTracker presence,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            var aviso = conversa!.Rename(currentUser.UserId!.Value, request.Title, clock.GetUtcNow());

            return await GravarENotificarAsync(conversa, aviso, db, hub, presence, ct);
        });

        group.MapPost("/members/{userId:guid}", async (
            Guid id,
            Guid userId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            IConversationNotifier notifier,
            IPresenceTracker presence,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            if (!await db.Profiles.AnyAsync(u => u.Id == userId, ct))
            {
                return Results.NotFound(new { message = "Usuário não existe." });
            }

            var aviso = conversa!.AddMember(currentUser.UserId!.Value, userId, clock.GetUtcNow());

            db.Messages.Add(aviso);
            await db.SaveChangesAsync(ct);

            // A ORDEM aqui importa, e errá-la produz uma falha intermitente das piores:
            //
            // Emitir para o grupo do SignalR só alcança quem já está inscrito nele. O novo
            // membro acabou de entrar no banco, mas a conexão que ele já tinha aberta continua
            // fora do grupo até alguém inscrevê-la. Notificar antes de inscrever faz o evento
            // se perder — e como o efeito depende de o usuário estar online naquele instante,
            // o bug aparece "às vezes", que é o tipo mais caro de investigar.
            //
            // Inscrever primeiro, notificar depois.
            var resposta = await MontarRespostaAsync(conversa, db, presence, ct);
            await notifier.MemberAddedAsync(resposta, userId);

            var nomes = await NomesAsync(db, [aviso.SenderId, aviso.SystemTargetId], ct);
            await hub.Clients.Group(ChatHub.GroupName(conversa.Id))
                .MessageReceived(ParaDto(aviso, nomes));

            return Results.NoContent();
        });

        group.MapDelete("/members/{userId:guid}", async (
            Guid id,
            Guid userId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            IPresenceTracker presence,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            var aviso = conversa!.RemoveMember(currentUser.UserId!.Value, userId, clock.GetUtcNow());

            return await GravarENotificarAsync(conversa, aviso, db, hub, presence, ct);
        });

        group.MapPatch("/members/{userId:guid}/role", async (
            Guid id,
            Guid userId,
            ChangeRoleRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            IPresenceTracker presence,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            if (!Enum.TryParse<MemberRole>(request.Role, ignoreCase: true, out var cargo))
            {
                return Results.Problem(
                    title: "Cargo inválido",
                    detail: $"'{request.Role}' não é um cargo conhecido. Use Member ou Admin.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var aviso = conversa!.ChangeRole(currentUser.UserId!.Value, userId, cargo, clock.GetUtcNow());

            return await GravarENotificarAsync(conversa, aviso, db, hub, presence, ct);
        });

        group.MapPost("/owner/{userId:guid}", async (
            Guid id,
            Guid userId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            IPresenceTracker presence,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            var aviso = conversa!.TransferOwnership(currentUser.UserId!.Value, userId, clock.GetUtcNow());

            return await GravarENotificarAsync(conversa, aviso, db, hub, presence, ct);
        });

        group.MapPatch("/mute", async (
            Guid id,
            MuteRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            CancellationToken ct) =>
        {
            var (conversa, erro) = await CarregarAsync(id, currentUser, db, ct);

            if (erro is not null)
            {
                return erro;
            }

            // Silenciar é preferência pessoal: não gera aviso de sistema nem notifica ninguém.
            conversa!.Mute(currentUser.UserId!.Value, request.Until);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return group;
    }

    private static async Task<(Conversation?, IResult?)> CarregarAsync(
        Guid id, ICurrentUser currentUser, DizidoDbContext db, CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return (null, Results.Unauthorized());
        }

        var conversa = await db.Conversations
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        // 404 uniforme para "não existe" e "não é sua": responder diferente permitiria
        // descobrir quais ids de conversa existem.
        return conversa is null || !conversa.IsActiveMember(me)
            ? (null, Results.NotFound())
            : (conversa, null);
    }

    /// <summary>
    /// Grava a alteração junto do aviso de sistema e o entrega a quem está na conversa.
    /// </summary>
    /// <remarks>
    /// Um único <c>SaveChangesAsync</c> para a mudança e o aviso: ou os dois acontecem, ou
    /// nenhum. Sem isso, o grupo poderia ser renomeado sem que o "o título mudou" aparecesse
    /// no fluxo — e ninguém saberia quem mudou.
    /// </remarks>
    private static async Task<IResult> GravarENotificarAsync(
        Conversation conversa,
        Message aviso,
        DizidoDbContext db,
        IHubContext<ChatHub, IChatClient> hub,
        IPresenceTracker presence,
        CancellationToken ct)
    {
        db.Messages.Add(aviso);
        await db.SaveChangesAsync(ct);

        var nomes = await NomesAsync(db, [aviso.SenderId, aviso.SystemTargetId], ct);
        var clientes = hub.Clients.Group(ChatHub.GroupName(conversa.Id));

        // Dois eventos, e os dois são necessários:
        //
        // O aviso entra no fluxo da conversa ("Ana mudou o nome do grupo"). Mas ele não diz
        // à lista lateral que o título é outro agora, nem que a lista de membros mudou —
        // esses dados vivem no ConversationResponse, não na mensagem.
        //
        // Sem o segundo evento, o outro participante vê "Ana mudou o nome do grupo para X"
        // enquanto a barra lateral continua exibindo o nome antigo, até ele recarregar.
        await clientes.MessageReceived(ParaDto(aviso, nomes));
        await clientes.ConversationAdded(await MontarRespostaAsync(conversa, db, presence, ct));

        return Results.NoContent();
    }

    private static async Task<Dictionary<Guid, string>> NomesAsync(
        DizidoDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var lista = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();

        return await db.Profiles
            .AsNoTracking()
            .Where(u => lista.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static MessageResponse ParaDto(Message m, Dictionary<Guid, string> nomes) =>
        new(m.Id, m.ConversationId, m.SenderId,
            nomes.GetValueOrDefault(m.SenderId, "(desconhecido)"),
            m.Body, m.ClientMessageId, m.ReplyToMessageId,
            m.SentAt, m.EditedAt, m.IsDeleted,
            m.Kind.ToString(),
            m.SystemEvent?.ToString(),
            m.SystemTargetId,
            m.SystemTargetId is { } alvo ? nomes.GetValueOrDefault(alvo) : null);

    private static async Task<ConversationResponse> MontarRespostaAsync(
        Conversation c, DizidoDbContext db, IPresenceTracker presence, CancellationToken ct)
    {
        var ids = c.Members.Select(m => m.UserId).ToList();
        var nomes = await NomesAsync(db, ids.Cast<Guid?>(), ct);
        var online = (await presence.FilterOnlineAsync(ids)).ToHashSet();

        return new ConversationResponse(
            c.Id, c.Type.ToString(), c.Title, c.AvatarUrl, c.CreatedAt, c.LastMessageAt,
            [.. c.Members.Where(m => m.IsActive).Select(m => new ConversationMemberResponse(
                m.UserId,
                nomes.GetValueOrDefault(m.UserId, "(desconhecido)"),
                m.Role.ToString(),
                m.LastReadMessageId,
                online.Contains(m.UserId)))]);
    }
}
