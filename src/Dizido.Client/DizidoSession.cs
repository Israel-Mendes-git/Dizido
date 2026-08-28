using Dizido.Contracts.Auth;

namespace Dizido.Client;

/// <summary>
/// A sessão do usuário no cliente: quem está logado e qual o access token atual.
/// </summary>
/// <remarks>
/// <para>
/// <b>O token fica em memória, não em localStorage.</b> Essa é a decisão de segurança mais
/// importante do lado do cliente. <c>localStorage</c> é legível por qualquer JavaScript que
/// rode na página — uma única falha de XSS (uma biblioteca comprometida, um trecho de HTML não
/// escapado) entrega a sessão inteira.
/// </para>
/// <para>
/// O custo é que recarregar a página perde o token. Mas não perde a sessão: o refresh token
/// está num cookie <c>HttpOnly</c>, invisível para JavaScript, e o cliente chama
/// <c>/auth/refresh</c> na inicialização para obter um access token novo. O usuário não percebe.
/// </para>
/// </remarks>
public sealed class DizidoSession
{
    private AuthResponse? _current;

    /// <summary>Disparado quando o usuário loga ou desloga.</summary>
    public event Action? Changed;

    public bool IsAuthenticated => _current is not null;

    public Guid? UserId => _current?.UserId;

    public string? DisplayName => _current?.DisplayName;

    public string? AccessToken => _current?.AccessToken;

    public DateTimeOffset? ExpiresAt => _current?.ExpiresAt;

    /// <summary>
    /// Está perto de expirar? O cliente renova antes de o token morrer, em vez de esperar
    /// tomar 401 no meio de uma ação do usuário.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now) =>
        _current is not null && now >= _current.ExpiresAt.AddMinutes(-2);

    public void Set(AuthResponse auth)
    {
        _current = auth;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _current = null;
        Changed?.Invoke();
    }
}
