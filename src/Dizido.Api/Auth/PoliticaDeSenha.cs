using Microsoft.AspNetCore.Identity;

namespace Dizido.Api.Auth;

/// <summary>
/// A política de senha do Dizido, em um lugar só.
/// </summary>
/// <remarks>
/// <para>
/// <b>Comprimento em vez de exigências decorativas.</b> Pedir maiúscula, dígito e símbolo
/// empurra as pessoas para <c>Senha123!</c> — previsível e curta. Um mínimo maior, sem
/// exigências, resiste melhor a ataque de dicionário.
/// </para>
/// <para>
/// Existe como método, e não como um bloco dentro do <c>Program.cs</c>, porque o teste de carga
/// também precisa criar contas. Duplicada nos dois lugares, ela sairia de sincronia no dia em
/// que alguém mudasse o mínimo — e o sintoma seria o teste de carga falhando ao semear, com uma
/// mensagem sobre maiúsculas que não tem nada a ver com carga.
/// </para>
/// </remarks>
public static class PoliticaDeSenha
{
    public const int ComprimentoMinimo = 10;

    public static void Aplicar(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = ComprimentoMinimo;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireDigit = false;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    }
}
