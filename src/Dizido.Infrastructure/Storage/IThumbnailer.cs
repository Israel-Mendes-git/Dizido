using SkiaSharp;

namespace Dizido.Infrastructure.Storage;

/// <summary>Dimensões da imagem original e a miniatura já gerada.</summary>
public sealed record ThumbnailResult(int Width, int Height, byte[] Thumbnail, string ThumbnailContentType);

public interface IThumbnailer
{
    /// <summary>
    /// Lê a imagem, devolve as dimensões dela e uma versão reduzida.
    /// </summary>
    /// <returns><c>null</c> se os bytes não formam uma imagem que a biblioteca saiba abrir.</returns>
    Task<ThumbnailResult?> CreateAsync(Stream original, CancellationToken ct = default);
}

/// <summary>Miniaturas com SkiaSharp.</summary>
/// <remarks>
/// <para>
/// A miniatura existe pela lista de conversas e pelo balão: carregar a foto original de 8 MB
/// para exibir um quadrado de 320 px desperdiça a franquia de dados de quem está no celular
/// e deixa a rolagem travada.
/// </para>
/// <para>
/// Sai sempre em JPEG, mesmo quando a original é PNG. Para uma prévia pequena a diferença de
/// qualidade não aparece, e o arquivo costuma ser uma fração do tamanho. A original continua
/// intacta no storage para quem abrir a imagem.
/// </para>
/// <para>
/// SkiaSharp e não ImageSharp: a partir da versão 4, o ImageSharp exige licença comercial paga.
/// O Skia é o motor gráfico do Chrome, e o pacote é MIT — o preço é carregar binários nativos
/// por plataforma, o que só aparece no tamanho da imagem de deploy.
/// </para>
/// </remarks>
public sealed class SkiaThumbnailer : IThumbnailer
{
    /// <summary>Maior lado da miniatura, em pixels.</summary>
    private const int LadoMaximo = 320;

    private const int Qualidade = 80;

    public async Task<ThumbnailResult?> CreateAsync(Stream original, CancellationToken ct = default)
    {
        // O Skia trabalha sobre bytes em memória, não sobre um stream de rede que pode
        // pausar no meio. Copiar antes evita decodificação parcial de um download lento.
        using var memoria = new MemoryStream();
        await original.CopyToAsync(memoria, ct);
        memoria.Position = 0;

        using var bitmap = SKBitmap.Decode(memoria);

        if (bitmap is null)
        {
            // Passou pelo magic number mas está corrompido do byte treze em diante. Quem
            // decide o que fazer com isso é quem chamou; aqui só relatamos que não deu.
            return null;
        }

        var largura = bitmap.Width;
        var altura = bitmap.Height;

        // Max preserva a proporção e nunca aumenta uma imagem que já é menor que o limite —
        // esticar uma miniatura de 100 px para 320 só gera borrão maior.
        var escala = Math.Min(1f, (float)LadoMaximo / Math.Max(largura, altura));

        var destino = new SKSizeI(
            Math.Max(1, (int)Math.Round(largura * escala)),
            Math.Max(1, (int)Math.Round(altura * escala)));

        using var reduzido = bitmap.Resize(destino, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        if (reduzido is null)
        {
            return null;
        }

        using var imagem = SKImage.FromBitmap(reduzido);
        using var dados = imagem.Encode(SKEncodedImageFormat.Jpeg, Qualidade);

        return new ThumbnailResult(largura, altura, dados.ToArray(), "image/jpeg");
    }
}
