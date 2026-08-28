namespace Dizido.Infrastructure.Storage;

/// <summary>Configuração do object storage (MinIO em desenvolvimento, S3 e afins em produção).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Endereço que a <b>API</b> usa para falar com o storage.</summary>
    /// <remarks>
    /// Em produção com contêineres isto costuma ser um nome interno da rede
    /// (<c>http://storage:9000</c>), inalcançável de fora.
    /// </remarks>
    public string Endpoint { get; set; } = "http://localhost:9000";

    /// <summary>
    /// Endereço que o <b>navegador</b> usa. Quando vazio, é o mesmo do <see cref="Endpoint"/>.
    /// </summary>
    /// <remarks>
    /// Existe porque a assinatura de uma URL temporária <b>inclui o host</b>. Assinar com o
    /// nome interno e depois trocar o host por um público invalida a assinatura, e o storage
    /// responde 403 sem explicar por quê. Por isso as URLs assinadas são geradas por um cliente
    /// configurado com este endereço, e as operações servidor-a-servidor pelo outro.
    /// </remarks>
    public string? PublicEndpoint { get; set; }

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = "dizido";

    /// <summary>
    /// O MinIO não usa regiões, mas o SDK da AWS exige uma para compor a assinatura.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Caminho (<c>host/bucket/chave</c>) em vez de subdomínio (<c>bucket.host/chave</c>).
    /// </summary>
    /// <remarks>
    /// Obrigatório com MinIO local: <c>dizido.localhost</c> não resolve em DNS nenhum.
    /// </remarks>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Validade da URL de upload. Curta de propósito: ela é uma autorização para escrever no
    /// bucket, e quem a interceptar pode usá-la enquanto valer.
    /// </summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Validade da URL de download. Precisa durar o suficiente para o navegador carregar a
    /// imagem e o usuário reabrir a conversa sem pedir tudo de novo.
    /// </summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);
}
