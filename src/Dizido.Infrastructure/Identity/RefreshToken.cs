using System.Security.Cryptography;
using System.Text;

namespace Dizido.Infrastructure.Identity;

/// <summary>
/// Token de renovação de sessão, de vida longa, guardado no banco.
/// </summary>
/// <remarks>
/// <para><b>Por que existe:</b> o access token (JWT) é verificado só pela assinatura, sem consultar
/// o banco — rápido, mas por isso mesmo <b>não dá para revogá-lo</b>. A solução usual é fazê-lo
/// durar pouco (15 min) e usar este token de vida longa para obter um novo quando expira. Revogar
/// uma sessão é apagar a linha daqui; o access token que restar morre em minutos.</para>
///
/// <para><b>Por que só o hash:</b> o valor em claro nunca é gravado. Se alguém obtiver acesso de
/// leitura ao banco, um hash não permite se passar por ninguém — é o mesmo raciocínio de nunca
/// guardar senha em claro.</para>
///
/// <para><b>Rotação e detecção de roubo:</b> cada uso troca o token por um novo e revoga o antigo,
/// registrando o substituto em <see cref="ReplacedByTokenId"/>. Se um token já revogado for
/// apresentado, só há duas explicações: ou o cliente perdeu a resposta da renovação, ou alguém
/// roubou o token. Como não dá para distinguir, tratamos como roubo e derrubamos a família
/// inteira de tokens daquela sessão. O usuário refaz o login; o invasor perde o acesso.</para>
/// </remarks>
public sealed class RefreshToken
{
    private RefreshToken() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 do valor entregue ao cliente. O valor em claro não é gravado.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Token que substituiu este na rotação. Encadeia a família da sessão.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// Gera um novo token. Devolve a entidade (para gravar) e o valor em claro
    /// (para entregar ao cliente uma única vez).
    /// </summary>
    public static (RefreshToken Entity, string PlainValue) Create(
        Guid userId,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        // 32 bytes de aleatoriedade criptográfica. Guid.NewGuid() NÃO serve aqui:
        // ele não é gerado por um RNG criptográfico e tem bits fixos de versão,
        // o que reduz a entropia real. Para segredo, RandomNumberGenerator.
        var plain = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            TokenHash = Hash(plain),
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
        };

        return (entity, plain);
    }

    public void Revoke(DateTimeOffset now, Guid? replacedByTokenId = null)
    {
        RevokedAt ??= now;
        ReplacedByTokenId ??= replacedByTokenId;
    }

    public static string Hash(string plainValue) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainValue)));
}
