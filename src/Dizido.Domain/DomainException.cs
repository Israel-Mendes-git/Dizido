namespace Dizido.Domain;

/// <summary>
/// Lançada quando uma regra do domínio é violada — por exemplo, tentar criar um
/// grupo sem título, ou editar mensagem de outra pessoa.
/// </summary>
/// <remarks>
/// Ter um tipo próprio (em vez de <see cref="InvalidOperationException"/>) permite que a API
/// traduza isso para HTTP 400/409 num lugar só, sem cada endpoint repetir try/catch.
/// </remarks>
public sealed class DomainException(string message) : Exception(message)
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new DomainException(message);
        }
    }
}
