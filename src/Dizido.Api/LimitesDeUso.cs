using System.Security.Claims;

namespace Dizido.Api;

/// <summary>
/// Os nomes das políticas de limite de uso, e como decidir a quem cada limite se aplica.
/// </summary>
/// <remarks>
/// Constantes em vez de strings soltas nos endpoints: errar o nome de uma política em
/// <c>RequireRateLimiting</c> não é erro de compilação, e o efeito de errar é o endpoint ficar
/// sem limite nenhum — falha silenciosa que só aparece quando alguém abusa.
/// </remarks>
internal static class LimitesDeUso
{
    public const string Auth = "auth";

    public const string Mensagens = "mensagens";

    public const string Uploads = "uploads";

    public const string Reacoes = "reacoes";

    /// <summary>
    /// A chave que separa um usuário do outro na contagem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O id do usuário autenticado quando existe; o IP como último recurso. A ordem importa:
    /// começar pelo IP colocaria uma escola inteira na mesma cota.
    /// </para>
    /// <para>
    /// O id vem da claim de um token já validado pelo middleware de autenticação — que roda
    /// antes do limitador no pipeline. Um cliente não consegue escolher a própria partição
    /// sem antes forjar um JWT assinado.
    /// </para>
    /// </remarks>
    public static string ParticaoDe(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var usuario = http.User.FindFirstValue(ClaimTypes.NameIdentifier);

        return string.IsNullOrEmpty(usuario)
            ? $"ip:{http.Connection.RemoteIpAddress}"
            : $"usuario:{usuario}";
    }
}
