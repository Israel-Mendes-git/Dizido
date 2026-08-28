using Microsoft.AspNetCore.Identity;

namespace Dizido.Infrastructure.Identity;

/// <summary>
/// As credenciais de um usuário: email, hash de senha, confirmação, bloqueio por tentativas.
/// Tudo gerenciado pelo ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// <para>
/// Esta entidade é <b>irmã</b> de <c>Dizido.Domain.Entities.User</c>, não a mesma coisa. As duas
/// compartilham o <c>Id</c> (relação 1:1 por chave), mas vivem em camadas diferentes:
/// </para>
/// <list type="bullet">
/// <item><c>DizidoUser</c> — credenciais. Fica na Infrastructure porque herda de
/// <see cref="IdentityUser{TKey}"/>, que vem de um pacote que o Domain não pode referenciar.</item>
/// <item><c>User</c> — perfil público (nome de exibição, avatar, visto por último). Fica no
/// Domain, sem dependência nenhuma, e é o que o resto do app usa o tempo todo.</item>
/// </list>
/// <para>
/// A separação não é só técnica: quase todo o código precisa saber o nome de exibição de alguém,
/// e quase nenhum precisa tocar em hash de senha. Manter os dois juntos faria a informação
/// sensível trafegar junto com a pública em toda consulta.
/// </para>
/// <para>
/// Herdamos de <c>IdentityUser&lt;Guid&gt;</c> (e não do padrão, com chave string) para que a
/// chave seja o mesmo UUIDv7 do perfil.
/// </para>
/// </remarks>
public sealed class DizidoUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
}
