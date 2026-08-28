using Dizido.Domain.Entities;
using Dizido.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace Dizido.Infrastructure.Persistence;

/// <summary>
/// Sessão de trabalho com o banco. Rastreia as entidades carregadas, acumula as alterações
/// e as grava todas de uma vez em <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>,
/// dentro de uma única transação.
/// </summary>
/// <remarks>
/// <para>
/// Registrado como <c>Scoped</c>: uma instância por requisição HTTP. Nunca Singleton —
/// o DbContext não é thread-safe, e duas requisições simultâneas corromperiam o rastreamento.
/// </para>
/// <para>
/// Herda de <see cref="IdentityDbContext{TUser,TRole,TKey}"/> para que as tabelas do Identity
/// (usuários, papéis, claims, logins externos) e as do domínio fiquem no mesmo banco e na mesma
/// transação. Alternativa seria um segundo DbContext só para identidade — mais isolamento, mas
/// aí criar um usuário e o perfil dele deixaria de ser atômico.
/// </para>
/// </remarks>
public sealed class DizidoDbContext(DbContextOptions<DizidoDbContext> options)
    : IdentityDbContext<DizidoUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // A base cria o mapeamento das tabelas do Identity. Precisa vir ANTES das nossas
        // configurações, senão sobrescreveria qualquer ajuste que façamos nelas.
        base.OnModelCreating(builder);

        // Varre este assembly e aplica toda classe que implemente IEntityTypeConfiguration<T>.
        // Sem isto, cada configuração nova precisaria ser registrada à mão aqui — e um
        // esquecimento vira "por que essa coluna virou nvarchar(max)?" três semanas depois.
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
