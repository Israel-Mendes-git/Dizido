using Dizido.Domain.Entities;
using Dizido.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeamento de <see cref="UserProfile"/> para tabela.
/// </summary>
/// <remarks>
/// As configurações ficam aqui, e não como atributos ([Table], [MaxLength]) nas entidades,
/// por um motivo estrutural: atributos exigiriam que o Dizido.Domain referenciasse o EF Core,
/// e a independência do Domain é justamente o que permite testá-lo sem banco.
/// </remarks>
internal sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profiles");

        builder.HasKey(u => u.Id);

        // Sem ValueGeneratedOnAdd: o Id é gerado pelo domínio (UUIDv7), não pelo banco.
        // Deixar o banco gerar produziria UUIDv4 aleatório e perderíamos a ordem temporal.
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.DisplayName)
            .HasMaxLength(UserProfile.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(u => u.AvatarUrl).HasMaxLength(500);

        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.LastSeenAt).IsRequired();

        // Relação 1:1 por chave compartilhada: o Id do perfil É o Id da conta no Identity.
        // Não existe coluna extra de ligação — a própria chave primária é a chave estrangeira.
        // Consequências: não dá para criar perfil sem conta, nem conta com dois perfis, e
        // apagar a conta leva o perfil junto. Tudo garantido pelo banco, não por código.
        builder.HasOne<DizidoUser>()
            .WithOne()
            .HasForeignKey<UserProfile>(u => u.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
