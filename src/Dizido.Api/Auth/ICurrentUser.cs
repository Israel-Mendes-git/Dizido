using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Dizido.Api.Auth;

/// <summary>Quem está fazendo esta requisição.</summary>
public interface ICurrentUser
{
    /// <summary>Id do usuário autenticado, ou null se a requisição é anônima.</summary>
    Guid? UserId { get; }
}

/// <summary>
/// Lê o id do usuário das claims do token já validado pelo middleware de autenticação.
/// </summary>
/// <remarks>
/// <para>
/// Este é o ponto que a Fase 1 antecipou: os endpoints sempre pediram <see cref="ICurrentUser"/>
/// em vez de ler um cabeçalho. Trocar a implementação provisória por esta foi <b>uma linha</b>
/// no <c>Program.cs</c>; nenhum endpoint precisou mudar.
/// </para>
/// <para>
/// Quando este código roda, o token já foi verificado: assinatura conferida, expiração checada,
/// emissor e audiência validados. Se algo estivesse errado, o middleware teria respondido 401
/// antes de chegar aqui. Por isso confiar na claim é seguro — ao contrário do cabeçalho anterior.
/// </para>
/// </remarks>
public sealed class JwtCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            // O ASP.NET Core reescreve "sub" para ClaimTypes.NameIdentifier por padrão.
            // Procuramos os dois nomes para não depender desse detalhe de configuração.
            var value = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }
}
