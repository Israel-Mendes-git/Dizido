namespace Dizido.Domain.Media;

/// <summary>
/// Identifica o formato de um arquivo pelos seus primeiros bytes.
/// </summary>
/// <remarks>
/// <para>
/// O <c>Content-Type</c> que o cliente envia é só uma afirmação, e afirmação de cliente não
/// vale nada: quem sobe o arquivo escolhe o cabeçalho. Já os primeiros bytes de um PNG são
/// sempre os mesmos oito — é uma característica do formato, não uma promessa de quem enviou.
/// </para>
/// <para>
/// Isso não é antivírus e não pretende ser. Serve para uma pergunta só: "isto aqui é mesmo
/// uma das quatro imagens que a gente exibe dentro da página?". Se não for, vira download,
/// e o navegador nunca interpreta o conteúdo.
/// </para>
/// </remarks>
public static class MagicNumber
{
    /// <summary>Quantos bytes bastam para decidir. O WebP é o mais exigente: precisa de 12.</summary>
    public const int BytesNecessarios = 12;

    /// <summary>
    /// O tipo indicado pelos bytes iniciais, ou <c>null</c> se não for um formato conhecido.
    /// </summary>
    public static string? Detectar(ReadOnlySpan<byte> inicio)
    {
        if (Comeca(inicio, [0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (Comeca(inicio, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        // "GIF87a" e "GIF89a" — as duas versões do formato ainda em circulação.
        if (Comeca(inicio, "GIF87a"u8) || Comeca(inicio, "GIF89a"u8))
        {
            return "image/gif";
        }

        // WebP é um contêiner RIFF: "RIFF", quatro bytes de tamanho, e só então "WEBP".
        // Conferir só o "RIFF" aceitaria um WAV como imagem.
        if (Comeca(inicio, "RIFF"u8) && inicio.Length >= 12 && inicio[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private static bool Comeca(ReadOnlySpan<byte> conteudo, ReadOnlySpan<byte> assinatura) =>
        conteudo.Length >= assinatura.Length && conteudo[..assinatura.Length].SequenceEqual(assinatura);
}
