using Dizido.Api.Attachments;
using Dizido.Api.Auth;
using Dizido.Api.Observabilidade;
using Dizido.Api.Realtime;
using Dizido.Contracts.Attachments;
using Dizido.Contracts.Messages;
using Dizido.Contracts.Realtime;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
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
            AttachmentPresenter presenter,
            DizidoMetrics metrics,
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
                return Results.Ok(await ToResponseAsync(duplicate, db, presenter, ct));
            }

            var conversation = await db.Conversations
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conversation is null || !conversation.IsActiveMember(me))
            {
                return Results.NotFound();
            }

            Attachment? attachment = null;

            if (request.AttachmentId is { } attachmentId)
            {
                attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);

                if (attachment is null)
                {
                    return Results.NotFound(new { message = "Anexo não encontrado." });
                }
            }

            // Toda a validação (membro ativo, corpo não vazio, tamanho, e se o anexo é mesmo
            // desta conversa e desta pessoa) mora no domínio. Se algo estiver errado, sai
            // DomainException e o DomainExceptionHandler converte em 400 — este endpoint não
            // tem um único try/catch.
            var message = conversation.PostMessage(
                me,
                request.Body,
                request.ClientMessageId,
                clock.GetUtcNow(),
                request.ReplyToMessageId,
                attachment);

            db.Messages.Add(message);

            // Uma única transação grava a mensagem E o LastMessageAt da conversa, que o
            // PostMessage adiantou. Ou os dois acontecem, ou nenhum: a lista de conversas
            // nunca mostra "agora mesmo" para uma conversa cuja mensagem não foi gravada.
            await db.SaveChangesAsync(ct);

            var response = await ToResponseAsync(message, db, presenter, ct);

            metrics.MensagemEnviada(message.Kind.ToString());

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

        // Editar. As regras — só o autor, não edita apagada, não edita aviso de sistema — já
        // estavam no domínio desde a Fase 1, testadas, e sem nenhuma porta que chegasse até elas.
        group.MapPatch("/{messageId:guid}", async (
            Guid conversationId,
            Guid messageId,
            EditMessageRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            TimeProvider clock,
            IHubContext<ChatHub, IChatClient> hub,
            AttachmentPresenter presenter,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var mensagem = await CarregarParaAlterarAsync(db, conversationId, messageId, me, ct);

            if (mensagem is null)
            {
                return Results.NotFound();
            }

            mensagem.Edit(me, request.Body, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);

            var resposta = await ToResponseAsync(mensagem, db, presenter, ct);

            // Reaproveita o MessageReceived em vez de inventar um evento de edição: o cliente
            // já casa a mensagem pelo ClientMessageId e substitui no lugar. Um evento novo
            // exigiria um segundo caminho de atualização fazendo exatamente o mesmo.
            await hub.Clients.Group(ChatHub.GroupName(conversationId)).MessageReceived(resposta);

            return Results.Ok(resposta);
        });

        // Apagar. Soft delete: a linha fica, o corpo é limpo. Apagar de verdade quebraria as
        // respostas que apontam para ela e as marcas de leitura dos membros.
        group.MapDelete("/{messageId:guid}", async (
            Guid conversationId,
            Guid messageId,
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

            var conversa = await db.Conversations
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conversa is null || !conversa.IsActiveMember(me))
            {
                return Results.NotFound();
            }

            var mensagem = await db.Messages.FirstOrDefaultAsync(
                m => m.Id == messageId && m.ConversationId == conversationId, ct);

            if (mensagem is null)
            {
                return Results.NotFound();
            }

            // Quem modera aqui é admin ou dono do grupo. A pergunta é feita à conversa, que é
            // quem sabe de cargos — o endpoint não interpreta MemberRole por conta própria.
            var ehModerador = conversa.FindMember(me)?.Role >= MemberRole.Admin;

            mensagem.Delete(me, clock.GetUtcNow(), ehModerador);
            await db.SaveChangesAsync(ct);

            await hub.Clients.Group(ChatHub.GroupName(conversationId))
                .MessageDeleted(new MessageDeletedEvent(conversationId, messageId, clock.GetUtcNow()));

            return Results.NoContent();
        });

        group.MapGet("/", async (
            Guid conversationId,
            ICurrentUser currentUser,
            DizidoDbContext db,
            AttachmentPresenter presenter,
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

            var anexos = await AnexosAsync(db, presenter, messages, ct);
            var citacoes = await CitacoesAsync(db, messages, ct);

            var items = messages
                .Select(m => ToResponse(m, names.GetValueOrDefault(m.SenderId, "(desconhecido)"), anexos, citacoes))
                .ToList();

            return Results.Ok(new MessagePage(items, hasMore ? messages[^1].Id : null));
        });

        return group;
    }

    /// <summary>
    /// Carrega uma mensagem para alteração, conferindo antes que quem pede participa da conversa.
    /// </summary>
    /// <remarks>
    /// Duas checagens em vez de uma consulta só com join: a participação é conferida primeiro,
    /// para que um não-membro receba 404 sem descobrir se aquela mensagem existe. Fundindo as
    /// duas, o mesmo 404 sairia — mas a diferença voltaria a aparecer no dia em que alguém
    /// otimizasse a consulta sem perceber a intenção.
    /// </remarks>
    private static async Task<Message?> CarregarParaAlterarAsync(
        DizidoDbContext db, Guid conversationId, Guid messageId, Guid me, CancellationToken ct)
    {
        var ehMembro = await db.ConversationMembers.AnyAsync(
            m => m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null, ct);

        return ehMembro
            ? await db.Messages.FirstOrDefaultAsync(
                m => m.Id == messageId && m.ConversationId == conversationId, ct)
            : null;
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
        AttachmentPresenter presenter,
        CancellationToken ct)
    {
        var name = await db.Profiles
            .AsNoTracking()
            .Where(u => u.Id == message.SenderId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);

        var anexos = await AnexosAsync(db, presenter, [message], ct);
        var citacoes = await CitacoesAsync(db, [message], ct);

        return ToResponse(message, name ?? "(desconhecido)", anexos, citacoes);
    }

    /// <summary>
    /// Busca de uma vez os anexos de um lote de mensagens e já os apresenta.
    /// </summary>
    /// <remarks>
    /// Uma consulta para a página inteira, e não uma por mensagem. É o mesmo N+1 que a lista
    /// de conversas evita ao resolver os nomes: cinquenta mensagens com foto virariam
    /// cinquenta idas ao banco.
    /// </remarks>
    private static async Task<Dictionary<Guid, AttachmentResponse>> AnexosAsync(
        DizidoDbContext db,
        AttachmentPresenter presenter,
        IReadOnlyList<Message> messages,
        CancellationToken ct)
    {
        var ids = messages
            .Where(m => m.AttachmentId is not null && !m.IsDeleted)
            .Select(m => m.AttachmentId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var anexos = await db.Attachments
            .AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(ct);

        return anexos.ToDictionary(a => a.Id, presenter.Present);
    }

    /// <summary>
    /// Monta as citações de um lote de mensagens: uma consulta para a página inteira.
    /// </summary>
    /// <remarks>
    /// O mesmo cuidado com N+1 dos nomes e dos anexos. Cinquenta respostas numa conversa
    /// movimentada virariam cinquenta idas ao banco só para desenhar as citações.
    /// </remarks>
    private static async Task<Dictionary<Guid, MessageReplyPreview>> CitacoesAsync(
        DizidoDbContext db,
        IReadOnlyList<Message> messages,
        CancellationToken ct)
    {
        var ids = messages
            .Where(m => m.ReplyToMessageId is not null)
            .Select(m => m.ReplyToMessageId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var originais = await db.Messages
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.SenderId, m.Body, m.DeletedAt, m.AttachmentId })
            .ToListAsync(ct);

        var autores = await db.Profiles
            .AsNoTracking()
            .Where(u => originais.Select(o => o.SenderId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return originais.ToDictionary(
            o => o.Id,
            o => new MessageReplyPreview(
                o.Id,
                autores.GetValueOrDefault(o.SenderId, "(desconhecido)"),
                Trecho(o.Body, o.DeletedAt is not null, o.AttachmentId is not null),
                o.DeletedAt is not null));
    }

    /// <summary>Encurta o corpo para caber na citação.</summary>
    private static string Trecho(string corpo, bool apagada, bool temAnexo)
    {
        if (apagada)
        {
            return "mensagem apagada";
        }

        var texto = corpo.Trim();

        if (texto.Length == 0)
        {
            // Foto sem legenda. A citação precisa dizer alguma coisa, senão fica um retângulo
            // vazio pendurado acima da resposta.
            return temAnexo ? "arquivo" : string.Empty;
        }

        const int Limite = 90;

        return texto.Length <= Limite ? texto : texto[..Limite] + "…";
    }

    private static MessageResponse ToResponse(
        Message m,
        string senderDisplayName,
        Dictionary<Guid, AttachmentResponse> anexos,
        Dictionary<Guid, MessageReplyPreview> citacoes,
        string? targetName = null) =>
        new(m.Id, m.ConversationId, m.SenderId, senderDisplayName, m.Body, m.ClientMessageId,
            m.ReplyToMessageId, m.SentAt, m.EditedAt, m.IsDeleted,
            m.Kind.ToString(), m.SystemEvent?.ToString(), m.SystemTargetId, targetName,

            // Mensagem apagada não devolve anexo: o balão vira "esta mensagem foi apagada",
            // e a foto não tem por que continuar aparecendo.
            m.AttachmentId is { } id && !m.IsDeleted ? anexos.GetValueOrDefault(id) : null,

            m.ReplyToMessageId is { } original ? citacoes.GetValueOrDefault(original) : null);
}
