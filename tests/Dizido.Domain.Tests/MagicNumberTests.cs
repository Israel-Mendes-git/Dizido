using System.Text;
using Dizido.Domain.Media;

namespace Dizido.Domain.Tests;

public sealed class MagicNumberTests
{
    [Fact]
    public void ReconheceOsQuatroFormatosDeImagem()
    {
        Assert.Equal("image/jpeg", MagicNumber.Detectar([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", MagicNumber.Detectar([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/gif", MagicNumber.Detectar(Encoding.ASCII.GetBytes("GIF87a")));
        Assert.Equal("image/gif", MagicNumber.Detectar(Encoding.ASCII.GetBytes("GIF89a")));
        Assert.Equal("image/webp", MagicNumber.Detectar(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP")));
    }

    /// <summary>
    /// WebP e WAV começam iguais: os dois são contêineres RIFF. Parar no "RIFF" aceitaria
    /// um arquivo de áudio como imagem.
    /// </summary>
    [Fact]
    public void RiffQueNaoEhWebpNaoPassa()
    {
        Assert.Null(MagicNumber.Detectar(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE")));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><script>")]
    [InlineData("%PDF-1.7")]
    [InlineData("PK")]
    public void ConteudoQueNaoEhImagemNaoEhReconhecido(string conteudo)
    {
        Assert.Null(MagicNumber.Detectar(Encoding.ASCII.GetBytes(conteudo)));
    }

    /// <summary>
    /// Um arquivo curto demais para conter assinatura nenhuma não pode virar exceção de
    /// índice — o servidor lê os primeiros bytes do que o cliente subiu, e ele pode ter
    /// subido três bytes.
    /// </summary>
    [Fact]
    public void ConteudoCurtoDemaisNaoQuebra()
    {
        Assert.Null(MagicNumber.Detectar([]));
        Assert.Null(MagicNumber.Detectar([0xFF]));
        Assert.Null(MagicNumber.Detectar([0x89, 0x50]));

        // "RIFF" sozinho, sem os 12 bytes que o WebP exige.
        Assert.Null(MagicNumber.Detectar(Encoding.ASCII.GetBytes("RIFF")));
    }

    /// <summary>
    /// O que importa é o começo. Um PNG com lixo depois continua sendo um PNG para efeito
    /// de decisão — a validação seguinte, ao gerar a miniatura, é que rejeita o corrompido.
    /// </summary>
    [Fact]
    public void OQueVemDepoisDaAssinaturaNaoInterfere()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x11, 0x22, 0x33, 0x44];

        Assert.Equal("image/png", MagicNumber.Detectar(png));
    }
}
