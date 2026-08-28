namespace Dizido.Api.Auth;

/// <summary>Configuração da emissão e validação de tokens. Vem de appsettings / variáveis de ambiente.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Quem emitiu o token. Validado na entrada.</summary>
    public string Issuer { get; init; } = "dizido";

    /// <summary>Para quem o token vale. Validado na entrada.</summary>
    public string Audience { get; init; } = "dizido";

    /// <summary>
    /// Chave secreta da assinatura HMAC-SHA256. Precisa de pelo menos 32 bytes.
    /// </summary>
    /// <remarks>
    /// Em desenvolvimento fica no appsettings.Development.json, que está no git — e por isso
    /// <b>não pode ser a mesma de produção</b>. Em produção vem de variável de ambiente
    /// (<c>Jwt__SigningKey</c>) ou de um cofre de segredos. Se esta chave vazar, qualquer um
    /// forja tokens válidos para qualquer usuário.
    /// </remarks>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>
    /// Validade do access token. Curta de propósito: como ele é validado só pela assinatura,
    /// sem consultar o banco, não há como revogá-lo antes de expirar.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Validade do refresh token. Longa, mas revogável a qualquer momento.</summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
}
