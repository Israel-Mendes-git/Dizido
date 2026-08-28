using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace Dizido.Infrastructure.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> sobre o protocolo S3.
/// </summary>
/// <remarks>
/// Dois clientes, e não um. Um aponta para o endereço interno e faz o trabalho de servidor
/// (conferir, gravar miniatura, apagar); o outro aponta para o endereço público e serve só
/// para <b>assinar</b> URLs, porque a assinatura carrega o host dentro dela. Em desenvolvimento
/// os dois endereços são o mesmo e o segundo cliente nem chega a ser criado.
/// </remarks>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _interno;
    private readonly AmazonS3Client _assinador;

    public S3ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;

        var credenciais = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);

        _interno = Criar(credenciais, _options.Endpoint);

        _assinador = string.IsNullOrWhiteSpace(_options.PublicEndpoint)
            || string.Equals(_options.PublicEndpoint, _options.Endpoint, StringComparison.OrdinalIgnoreCase)
                ? _interno
                : Criar(credenciais, _options.PublicEndpoint);
    }

    private AmazonS3Client Criar(AWSCredentials credenciais, string endpoint) =>
        new(credenciais, new AmazonS3Config
        {
            ServiceURL = endpoint,

            // host/bucket/chave em vez de bucket.host/chave — "dizido.localhost" não resolve.
            ForcePathStyle = _options.ForcePathStyle,

            // O MinIO ignora a região, mas o SDK precisa de uma para compor a assinatura.
            AuthenticationRegion = _options.Region,
        });

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(_interno, _options.Bucket))
        {
            return;
        }

        await _interno.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, ct);
    }

    public Uri CreateUploadUrl(string key, string contentType) =>
        new(_assinador.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(_options.UploadUrlLifetime),

            // Amarrar o Content-Type na assinatura: o cliente é obrigado a enviar exatamente
            // este cabeçalho, senão o storage recusa. Não é validação de conteúdo — quem
            // garante o formato são os bytes, conferidos depois — mas fecha a porta para o
            // objeto ser gravado anunciando text/html e ser servido como tal.
            ContentType = contentType,
        }));

    public Uri CreateDownloadUrl(string key, string fileName, string contentType, bool showInline) =>
        new(_assinador.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(_options.DownloadUrlLifetime),

            // O objeto foi gravado com o tipo que o cliente declarou. Aqui mandamos o storage
            // responder com o tipo que o servidor confirmou, e com o nome original — que não
            // está na chave, justamente para não virar caminho.
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = contentType,
                ContentDisposition = ContentDisposition(fileName, showInline),
            },
        }));

    public async Task<ObjectInfo?> DescribeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await _interno.GetObjectMetadataAsync(_options.Bucket, key, ct);

            return new ObjectInfo(meta.ContentLength, meta.Headers.ContentType);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // O cliente pediu a URL e nunca subiu nada. É um caso esperado, não um erro.
            return null;
        }
    }

    public async Task<byte[]> ReadPrefixAsync(string key, int byteCount, CancellationToken ct = default)
    {
        // Só o começo do arquivo, e não ele inteiro: identificar o formato leva alguns bytes,
        // e baixar 50 MB para olhar os primeiros doze seria absurdo.
        using var resposta = await _interno.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            ByteRange = new ByteRange(0, byteCount - 1),
        }, ct);

        using var memoria = new MemoryStream();
        await resposta.ResponseStream.CopyToAsync(memoria, ct);

        return memoria.ToArray();
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var resposta = await _interno.GetObjectAsync(_options.Bucket, key, ct);

        return resposta.ResponseStream;
    }

    public async Task WriteAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _interno.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
        }, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _interno.DeleteObjectAsync(_options.Bucket, key, ct);
    }

    /// <summary>
    /// Monta o cabeçalho <c>Content-Disposition</c> com o nome do arquivo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>attachment</c> é o que garante que o navegador salve o arquivo em vez de tentar
    /// interpretá-lo. Só imagens confirmadas recebem <c>inline</c>.
    /// </para>
    /// <para>
    /// O nome vai duas vezes: <c>filename=</c> só com ASCII, para clientes antigos, e
    /// <c>filename*=</c> em UTF-8 percent-encoded (RFC 5987), que é o que preserva acentos.
    /// Sem o segundo, "relatório final.pdf" chega ao disco como "relatrio final.pdf".
    /// </para>
    /// </remarks>
    private static string ContentDisposition(string fileName, bool showInline)
    {
        var tipo = showInline ? "inline" : "attachment";

        var ascii = new string([.. fileName.Select(c => c is >= (char)32 and < (char)127 ? c : '_')]);
        var utf8 = PercentEncode(fileName);

        return $"{tipo}; filename=\"{ascii.Replace("\"", string.Empty, StringComparison.Ordinal)}\"; filename*=UTF-8''{utf8}";
    }

    private static string PercentEncode(string valor)
    {
        var resultado = new StringBuilder();

        foreach (var b in Encoding.UTF8.GetBytes(valor))
        {
            var c = (char)b;

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')
            {
                resultado.Append(c);
            }
            else
            {
                resultado.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return resultado.ToString();
    }

    public void Dispose()
    {
        _interno.Dispose();

        if (!ReferenceEquals(_assinador, _interno))
        {
            _assinador.Dispose();
        }
    }
}
