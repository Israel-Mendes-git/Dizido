using Dizido.Api.Auth;
using Dizido.Api.Reactions;
using Dizido.Api.Realtime;
using Dizido.Contracts.Reactions;
using Dizido.Contracts.Realtime;
using Dizido.Domain.Entities;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Dizido.Api.Endpoints;

/// <summary>
/// Reações com emoji.
/// </summary>
/// <remarks>
/// <para>
/// <b>Duas rotas, e não um "alternar".</b> Um único endpoint que inverte o estado seria mais
/// curto de escrever e errado de usar: o cliente reenvia quando a resposta se perde na rede, e
/// um alternar reenviado desfaz exatamente o que a primeira tentativa tinha feito. Com POST
/// para pôr e DELETE para tirar, o cliente declara o estado que quer — e repetir o pedido
/// chega no mesmo lugar. É a mesma ideia do <c>ClientMessageId</c> no envio de mensagem.
/// </para>
/// <para>
/// <b>Reagir não mexe no <c>LastMessageAt</c></b> e não conta como não lida. Se contasse, a
/// conversa saltaria para o topo da lista a cada polegar — e o ponto da reação, segundo o
/// próprio <c>docs/RUMO.md</c>, é tirar ruído do fluxo, não criar outro.
/// </para>
/// </remarks>
internal static class ReactionEndpoints
{
    public static RouteGroupBuilder MapReactionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/conversations/{conversationId:guid}/messages/{messageId:guid}/reactions")
            .WithTags("Reactions");

        group.MapPost("/", async (
            Guid conversationId,
            Guid messageId,
            ReactRequest request,
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

            var emoji = request.Emoji?.Trim() ?? string.Empty;

            // A paleta é conferida aqui, e não no domínio, porque ela é decisão de produto e
            // vive em Contracts para a interface poder desenhar o seletor com a MESMA lista.
            // Ver ReactionPalette. O que o domínio garante é a forma: um emoji, sem espaços,
            // dentro do tamanho — isso vale mesmo que a paleta mude amanhã.
            if (!ReactionPalette.Contem(emoji))
            {
                return Results.Problem(
                    detail: "Este emoji não está na paleta de reações do Dizido.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var mensagem = await CarregarAsync(db, conversationId, messageId, me, ct);

            if (mensagem is null)
            {
                return Results.NotFound();
            }

            // Já existe: nada a gravar e nada a anunciar. Devolver o estado atual com 200 é o
            // que torna o reenvio inofensivo — o cliente vê o mesmo resultado da primeira vez.
            var jaExiste = await db.Reactions.AnyAsync(
                r => r.MessageId == messageId && r.UserId == me && r.Emoji == emoji, ct);

            if (jaExiste)
            {
                return Results.Ok(await ReactionPresenter.DeUmaMensagemAsync(db, messageId, ct));
            }

            db.Reactions.Add(Reaction.Create(mensagem, me, emoji, clock.GetUtcNow()));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A checagem acima resolve o caso comum; esta rede pega a corrida real, de duas
                // requisições idênticas que passaram pela checagem antes de qualquer uma gravar.
                // Quem barra é a chave composta (MessageId, UserId, Emoji) — e o desfecho certo
                // aqui não é erro, é "já estava lá".
                db.ChangeTracker.Clear();

                return Results.Ok(await ReactionPresenter.DeUmaMensagemAsync(db, messageId, ct));
            }

            await hub.Clients.Group(ChatHub.GroupName(conversationId))
                .ReactionChanged(new MessageReactionEvent(conversationId, messageId, emoji, me, Added: true));

            return Results.Ok(await ReactionPresenter.DeUmaMensagemAsync(db, messageId, ct));
        }).RequireRateLimiting(LimitesDeUso.Reacoes);

        // Tirar a própria reação. O emoji vem na query e NÃO é conferido contra a paleta: o dia
        // em que um emoji sair da lista, quem já tinha reagido com ele precisa continuar
        // podendo desfazer — senão a reação fica presa no balão para sempre.
        group.MapDelete("/", async (
            Guid conversationId,
            Guid messageId,
            string emoji,
            ICurrentUser currentUser,
            DizidoDbContext db,
            IHubContext<ChatHub, IChatClient> hub,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            if (await CarregarAsync(db, conversationId, messageId, me, ct) is null)
            {
                return Results.NotFound();
            }

            var texto = emoji?.Trim() ?? string.Empty;

            var reacao = await db.Reactions.FirstOrDefaultAsync(
                r => r.MessageId == messageId && r.UserId == me && r.Emoji == texto, ct);

            // Não existe? Então o estado pedido já é o estado atual. Nada a anunciar.
            if (reacao is null)
            {
                return Results.Ok(await ReactionPresenter.DeUmaMensagemAsync(db, messageId, ct));
            }

            db.Reactions.Remove(reacao);
            await db.SaveChangesAsync(ct);

            await hub.Clients.Group(ChatHub.GroupName(conversationId))
                .ReactionChanged(new MessageReactionEvent(conversationId, messageId, texto, me, Added: false));

            return Results.Ok(await ReactionPresenter.DeUmaMensagemAsync(db, messageId, ct));
        }).RequireRateLimiting(LimitesDeUso.Reacoes);

        return group;
    }

    /// <summary>
    /// Carrega a mensagem, conferindo antes que quem pede participa da conversa.
    /// </summary>
    /// <remarks>
    /// A participação primeiro, e a mensagem depois: assim quem não é da conversa recebe 404
    /// sem descobrir se aquela mensagem existe. O mesmo cuidado — e o mesmo 404 uniforme — do
    /// resto da API.
    /// </remarks>
    private static async Task<Message?> CarregarAsync(
        DizidoDbContext db, Guid conversationId, Guid messageId, Guid me, CancellationToken ct)
    {
        var ehMembro = await db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null, ct);

        return ehMembro
            ? await db.Messages.AsNoTracking().FirstOrDefaultAsync(
                m => m.Id == messageId && m.ConversationId == conversationId, ct)
            : null;
    }
}
