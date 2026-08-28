namespace Dizido.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// Resposta de login/registro/renovação.
/// </summary>
/// <param name="AccessToken">
/// JWT de vida curta. O cliente guarda <b>em memória</b> e envia no cabeçalho
/// <c>Authorization: Bearer ...</c>.
/// </param>
/// <param name="ExpiresAt">Quando o access token expira, para o cliente renovar antes.</param>
/// <remarks>
/// O refresh token <b>não aparece aqui de propósito</b>: ele vai num cookie <c>HttpOnly</c>,
/// que JavaScript não consegue ler. Se ele viesse no corpo, o cliente teria que guardá-lo em
/// algum lugar acessível por script — e uma única falha de XSS entregaria a sessão inteira,
/// renovável por 30 dias. Com o cookie, um XSS consegue no máximo usar o access token que
/// expira em 15 minutos.
/// </remarks>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string DisplayName);
