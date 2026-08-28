namespace Dizido.Domain.Enums;

/// <summary>
/// Papel de um membro dentro de uma conversa. Os valores são crescentes em poder,
/// então dá para comparar com &gt;= (ex.: <c>role >= MemberRole.Admin</c>).
/// </summary>
public enum MemberRole
{
    Member = 1,
    Admin = 2,
    Owner = 3,
}
