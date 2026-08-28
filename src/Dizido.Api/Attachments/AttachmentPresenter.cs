using Dizido.Contracts.Attachments;
using Dizido.Domain.Entities;
using Dizido.Domain.Enums;
using Dizido.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Dizido.Api.Attachments;

/// <summary>
/// Transforma um <see cref="Attachment"/> do banco no DTO que o cliente recebe, assinando
/// as URLs de leitura na hora.
/// </summary>
/// <remarks>
/// <para>
/// As URLs não ficam guardadas em lugar nenhum: elas expiram, e uma URL gravada no banco
/// estaria morta na primeira vez que alguém abrisse a conversa no dia seguinte. Assinar é
/// uma operação local e barata — não há ida ao storage, só um HMAC sobre o caminho e o prazo.
/// </para>
/// <para>
/// É a mesma razão de o DTO devolver <c>UrlExpiresAt</c>: o cliente sabe quando parar de
/// confiar no que tem em mãos, em vez de descobrir por uma imagem quebrada.
/// </para>
/// </remarks>
public sealed class AttachmentPresenter(
    IObjectStorage storage,
    IOptions<StorageOptions> options,
    TimeProvider clock)
{
    public AttachmentResponse Present(Attachment anexo)
    {
        var ehImagem = anexo.Kind == AttachmentKind.Image;

        // inline só para imagem confirmada. Todo o resto desce com Content-Disposition:
        // attachment, e o navegador salva em vez de tentar interpretar.
        var url = storage.CreateDownloadUrl(anexo.StorageKey, anexo.FileName, anexo.ContentType, ehImagem);

        var miniatura = anexo.ThumbnailKey is { } chave
            ? storage.CreateDownloadUrl(chave, anexo.FileName, "image/jpeg", showInline: true).ToString()
            : null;

        return new AttachmentResponse(
            anexo.Id,
            anexo.FileName,
            anexo.ContentType,
            anexo.SizeBytes,
            anexo.Kind.ToString(),
            url.ToString(),
            miniatura,
            anexo.Width,
            anexo.Height,
            clock.GetUtcNow().Add(options.Value.DownloadUrlLifetime));
    }
}
