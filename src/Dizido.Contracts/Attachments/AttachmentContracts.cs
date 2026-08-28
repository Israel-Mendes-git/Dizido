namespace Dizido.Contracts.Attachments;

/// <summary>
/// Primeiro passo do upload: o cliente descreve o que pretende enviar e pede autorização.
/// </summary>
/// <param name="ContentType">
/// O que o cliente <b>afirma</b> estar enviando. O servidor usa isto só para escolher o limite
/// de tamanho — o formato de verdade é conferido nos bytes, depois que o arquivo chega.
/// </param>
/// <param name="SizeBytes">
/// Tamanho prometido. Serve para recusar um arquivo grande demais <b>antes</b> de gastar
/// banda subindo-o. O tamanho que vale é o que o storage relata no fim.
/// </param>
public sealed record RequestUploadRequest(string FileName, string ContentType, long SizeBytes);

/// <summary>A autorização de upload: para onde enviar, como, e até quando.</summary>
/// <param name="UploadUrl">
/// URL temporária para um <c>PUT</c> direto no storage, sem passar pela API.
/// </param>
/// <param name="ContentType">
/// O cabeçalho <c>Content-Type</c> que o <c>PUT</c> <b>precisa</b> enviar. Ele faz parte da
/// assinatura: mandar outro faz o storage recusar com 403.
/// </param>
public sealed record UploadTicketResponse(
    Guid AttachmentId,
    string UploadUrl,
    string ContentType,
    long MaxSizeBytes,
    DateTimeOffset ExpiresAt);

/// <summary>Um anexo pronto, como o cliente o enxerga.</summary>
/// <param name="Kind">"Image" ou "File". Decide se o cliente exibe no balão ou mostra um cartão.</param>
/// <param name="Url">
/// URL temporária de leitura. <b>Expira</b> — quando o navegador falhar ao carregar, peça de
/// novo em <c>GET /api/attachments/{id}</c> em vez de guardar esta para sempre.
/// </param>
/// <param name="Width">Dimensões da original, para o cliente reservar o espaço antes de carregar.</param>
public sealed record AttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Kind,
    string Url,
    string? ThumbnailUrl,
    int? Width,
    int? Height,
    DateTimeOffset UrlExpiresAt);
