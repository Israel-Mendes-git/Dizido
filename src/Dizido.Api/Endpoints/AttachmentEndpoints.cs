using Dizido.Api;
using Dizido.Api.Attachments;
using Dizido.Api.Auth;
using Dizido.Api.Observabilidade;
using Dizido.Contracts.Attachments;
using Dizido.Domain;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Domain.Media;
using Dizido.Infrastructure.Persistence;
using Dizido.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dizido.Api.Endpoints;

/// <summary>
/// Upload e download de anexos, em três passos.
/// </summary>
/// <remarks>
/// <para>
/// <b>1. Pedir.</b> O cliente descreve o arquivo e recebe uma URL temporária.
/// <b>2. Subir.</b> O cliente faz <c>PUT</c> direto no storage — a API não vê os bytes.
/// <b>3. Confirmar.</b> O servidor confere o que chegou e libera o anexo para uso.
/// </para>
/// <para>
/// O passo 3 é o que impede o cliente de mandar no arquivo. Sem ele, quem subisse um HTML
/// dizendo "isto é um PNG" teria uma página hospedada na origem do app, e o navegador de quem
/// abrisse executaria o script dela com a sessão da vítima — XSS armazenado.
/// </para>
/// </remarks>
internal static class AttachmentEndpoints
{
    public static RouteGroupBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api").WithTags("Attachments");

        group.MapPost("/conversations/{conversationId:guid}/attachments", async (
            Guid conversationId,
            RequestUploadRequest request,
            ICurrentUser currentUser,
            DizidoDbContext db,
            IObjectStorage storage,
            IOptions<StorageOptions> options,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            // Mesmo 404 uniforme dos outros endpoints: para quem não participa, a conversa
            // é indistinguível de uma que não existe.
            var ehMembro = await db.ConversationMembers.AnyAsync(
                m => m.ConversationId == conversationId && m.UserId == me && m.LeftAt == null, ct);

            if (!ehMembro)
            {
                return Results.NotFound();
            }

            // O limite é conferido AQUI, antes de qualquer byte subir. Recusar depois do
            // upload gastaria a banda de quem enviou para nada.
            var anexo = Attachment.Request(
                conversationId, me, request.FileName, request.ContentType, request.SizeBytes, clock.GetUtcNow());

            db.Attachments.Add(anexo);
            await db.SaveChangesAsync(ct);

            var url = storage.CreateUploadUrl(anexo.StorageKey, anexo.ContentType);

            return Results.Ok(new UploadTicketResponse(
                anexo.Id,
                url.ToString(),
                anexo.ContentType,
                anexo.Kind == AttachmentKind.Image ? Attachment.MaxImageBytes : Attachment.MaxFileBytes,
                clock.GetUtcNow().Add(options.Value.UploadUrlLifetime)));

        // O limite fica no pedido, e não na confirmação: é ele que reserva a linha no banco
        // e autoriza os 50 MB. A confirmação só existe para quem já passou por aqui.
        }).RequireRateLimiting(LimitesDeUso.Uploads);

        group.MapPost("/attachments/{id:guid}/complete", async (
            Guid id,
            ICurrentUser currentUser,
            DizidoDbContext db,
            IObjectStorage storage,
            IThumbnailer thumbnailer,
            AttachmentPresenter presenter,
            TimeProvider clock,
            DizidoMetrics metrics,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var anexo = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, ct);

            // Só quem pediu o upload confirma. Um membro qualquer da conversa não tem nada
            // que fazer aqui, e para todo o resto do mundo o anexo não existe.
            if (anexo is null || anexo.UploadedById != me)
            {
                return Results.NotFound();
            }

            // Idempotente: a resposta do "complete" pode ter se perdido na volta e o cliente
            // estar repetindo. Repetir não pode ser erro, senão o retry quebra o envio.
            if (anexo.IsReady)
            {
                return Results.Ok(presenter.Present(anexo));
            }

            var objeto = await storage.DescribeAsync(anexo.StorageKey, ct);

            if (objeto is null)
            {
                return Results.Problem(
                    title: "Arquivo não encontrado",
                    detail: "O upload não chegou ao storage. Tente enviar novamente.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            string tipoReal = anexo.ContentType;
            int? largura = null;
            int? altura = null;
            string? chaveDaMiniatura = null;

            if (anexo.Kind == AttachmentKind.Image)
            {
                // Doze bytes bastam para reconhecer os quatro formatos. Baixar a imagem
                // inteira só para olhar o começo seria desperdício.
                var inicio = await storage.ReadPrefixAsync(anexo.StorageKey, MagicNumber.BytesNecessarios, ct);
                var detectado = MagicNumber.Detectar(inicio);

                if (detectado is null || !Attachment.ImagensPermitidas.Contains(detectado))
                {
                    await DescartarAsync(storage, anexo, db, ct);

                    throw new DomainException(
                        "O conteúdo enviado não é uma imagem em formato aceito.");
                }

                await using var conteudo = await storage.OpenReadAsync(anexo.StorageKey, ct);
                var miniatura = await thumbnailer.CreateAsync(conteudo, ct);

                if (miniatura is null)
                {
                    // Assinatura certa, resto do arquivo corrompido. Só descobrimos aqui, ao
                    // tentar decodificar de verdade.
                    await DescartarAsync(storage, anexo, db, ct);

                    throw new DomainException("Não foi possível ler esta imagem.");
                }

                chaveDaMiniatura = $"{anexo.StorageKey}-thumb";

                await using (var bytes = new MemoryStream(miniatura.Thumbnail))
                {
                    await storage.WriteAsync(chaveDaMiniatura, bytes, miniatura.ThumbnailContentType, ct);
                }

                tipoReal = detectado;
                largura = miniatura.Width;
                altura = miniatura.Height;
            }

            anexo.Confirm(tipoReal, objeto.SizeBytes, clock.GetUtcNow(), largura, altura, chaveDaMiniatura);

            await db.SaveChangesAsync(ct);

            metrics.AnexoConfirmado(anexo.Kind.ToString(), anexo.SizeBytes);

            return Results.Ok(presenter.Present(anexo));
        });

        // Renovar as URLs de um anexo. Elas expiram, e uma conversa deixada aberta a tarde
        // inteira acabaria com imagens quebradas se o cliente não tivesse para onde voltar.
        group.MapGet("/attachments/{id:guid}", async (
            Guid id,
            ICurrentUser currentUser,
            DizidoDbContext db,
            AttachmentPresenter presenter,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } me)
            {
                return Results.Unauthorized();
            }

            var anexo = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

            if (anexo is null || !anexo.IsReady)
            {
                return Results.NotFound();
            }

            // A autorização é a da conversa do anexo — não a de quem o enviou. É o que
            // permite todo mundo do grupo ver a foto, e só eles.
            var ehMembro = await db.ConversationMembers.AnyAsync(
                m => m.ConversationId == anexo.ConversationId && m.UserId == me && m.LeftAt == null, ct);

            return ehMembro ? Results.Ok(presenter.Present(anexo)) : Results.NotFound();
        });

        return group;
    }

    /// <summary>
    /// Apaga os bytes recusados e a linha que os representava.
    /// </summary>
    /// <remarks>
    /// O objeto sai primeiro: se a ordem fosse invertida e o processo morresse no meio, o
    /// arquivo ficaria no bucket sem nenhuma linha apontando para ele — invisível para a
    /// faxina, e cobrado para sempre.
    /// </remarks>
    private static async Task DescartarAsync(
        IObjectStorage storage, Attachment anexo, DizidoDbContext db, CancellationToken ct)
    {
        await storage.DeleteAsync(anexo.StorageKey, ct);

        db.Attachments.Remove(anexo);
        await db.SaveChangesAsync(ct);
    }
}
