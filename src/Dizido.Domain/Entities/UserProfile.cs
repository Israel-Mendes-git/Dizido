namespace Dizido.Domain.Entities;

/// <summary>
/// Perfil público de um usuário: como ele aparece para os outros.
/// </summary>
/// <remarks>
/// Repare no que NÃO está aqui: email, senha, confirmação, tentativas de login. Essas são
/// credenciais, e ficam em <c>Dizido.Infrastructure.Identity.DizidoUser</c>, sob o ASP.NET Core
/// Identity, numa entidade que compartilha exatamente este mesmo <see cref="Id"/> (relação 1:1
/// por chave).
/// <para>
/// O motivo é prático, não purismo: o Identity traz um pacote com dependências que o Domain
/// não pode ter. E é uma boa separação de qualquer forma — 99% do código do app precisa saber
/// o nome de exibição de alguém, e 1% precisa mexer no hash de senha. Juntos, o dado sensível
/// viajaria de carona em toda consulta que só queria um nome.
/// </para>
/// <para>
/// O nome é <c>UserProfile</c>, e não <c>User</c>, para não colidir com o <c>Users</c> do
/// Identity — e porque descreve melhor o que a entidade é.
/// </para>
/// </remarks>
public sealed class UserProfile
{
    public const int MaxDisplayNameLength = 40;

    // O EF Core precisa de um construtor sem parâmetros para materializar linhas do banco.
    // Privado, para que o resto do código seja obrigado a usar Create() e passar pelas regras.
    private UserProfile() { }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = null!;

    public string? AvatarUrl { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static UserProfile Create(Guid id, string displayName, DateTimeOffset now)
    {
        ValidateDisplayName(displayName);

        return new UserProfile
        {
            Id = id,
            DisplayName = displayName.Trim(),
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    public void Rename(string displayName)
    {
        ValidateDisplayName(displayName);
        DisplayName = displayName.Trim();
    }

    public void SetAvatar(string? avatarUrl) => AvatarUrl = avatarUrl;

    /// <summary>Registra atividade. Alimenta o "visto por último".</summary>
    public void Touch(DateTimeOffset now)
    {
        if (now > LastSeenAt)
        {
            LastSeenAt = now;
        }
    }

    private static void ValidateDisplayName(string displayName)
    {
        DomainException.Require(
            !string.IsNullOrWhiteSpace(displayName),
            "O nome de exibição não pode ser vazio.");

        DomainException.Require(
            displayName.Trim().Length <= MaxDisplayNameLength,
            $"O nome de exibição não pode passar de {MaxDisplayNameLength} caracteres.");
    }
}
