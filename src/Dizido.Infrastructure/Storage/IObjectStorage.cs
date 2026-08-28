namespace Dizido.Infrastructure.Storage;

/// <summary>O que o storage relata sobre um objeto que já existe.</summary>
public sealed record ObjectInfo(long SizeBytes, string? ContentType);

/// <summary>
/// Guarda e devolve arquivos. Implementado sobre o protocolo S3 — o mesmo código serve
/// MinIO em desenvolvimento e AWS S3, Cloudflare R2 ou Backblaze B2 em produção.
/// </summary>
/// <remarks>
/// <para>
/// Repare no que <b>não</b> existe aqui: um método que receba o arquivo do usuário e o
/// encaminhe. É deliberado. Um upload atravessando a API ocupa uma thread e memória do
/// servidor durante todo o envio — com 50 pessoas mandando um arquivo de 40 MB numa rede
/// lenta, são 50 requisições presas por minutos. As URLs assinadas tiram o servidor do
/// caminho: ele só autoriza, e os bytes vão direto do navegador para o storage.
/// </para>
/// <para>
/// Os métodos que leem e escrevem bytes existem para o que o servidor <b>precisa</b> fazer
/// sozinho: conferir o começo do arquivo que chegou e gravar a miniatura.
/// </para>
/// </remarks>
public interface IObjectStorage
{
    /// <summary>Cria o bucket se ele ainda não existir.</summary>
    Task EnsureBucketAsync(CancellationToken ct = default);

    /// <summary>URL temporária que autoriza o cliente a fazer <c>PUT</c> nesta chave.</summary>
    Uri CreateUploadUrl(string key, string contentType);

    /// <summary>
    /// URL temporária de leitura, já instruindo o navegador sobre o nome e o tratamento.
    /// </summary>
    /// <param name="showInline">
    /// <c>true</c> exibe na página (imagens); <c>false</c> força o download com o nome original.
    /// </param>
    Uri CreateDownloadUrl(string key, string fileName, string contentType, bool showInline);

    /// <summary>Tamanho e tipo do objeto, ou <c>null</c> se ele não existe.</summary>
    Task<ObjectInfo?> DescribeAsync(string key, CancellationToken ct = default);

    /// <summary>Lê os primeiros bytes do objeto — o bastante para identificar o formato.</summary>
    Task<byte[]> ReadPrefixAsync(string key, int byteCount, CancellationToken ct = default);

    /// <summary>Abre o objeto inteiro para leitura.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task WriteAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
