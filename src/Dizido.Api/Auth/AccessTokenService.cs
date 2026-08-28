using Dizido.Infrastructure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Dizido.Api.Auth;

public interface IAccessTokenService
{
    string Create(DizidoUser user, DateTimeOffset now);
}

/// <summary>Emite os JWT de acesso.</summary>
/// <remarks>
/// Um JWT tem três partes separadas por ponto: cabeçalho, payload e assinatura, todas em
/// Base64Url. <b>O payload não é criptografado</b> — qualquer pessoa com o token consegue ler o
/// conteúdo em jwt.io. A assinatura só garante que ninguém alterou o que está lá. Portanto:
/// nunca coloque nada sigiloso nas claims.
/// </remarks>
public sealed class AccessTokenService(IOptions<JwtOptions> options) : IAccessTokenService
{
    private readonly JwtOptions _options = options.Value;

    public string Create(DizidoUser user, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            // "sub" (subject) é a claim padrão para "de quem é este token".
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),

            // "jti" (JWT ID) identifica esta emissão específica. Útil para rastrear nos logs
            // e para uma eventual lista de revogação.
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7(now).ToString()),
        ];

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(_options.AccessTokenLifetime).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
