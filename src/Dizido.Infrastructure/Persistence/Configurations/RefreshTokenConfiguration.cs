using Dizido.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        // SHA-256 em hexadecimal tem sempre 64 caracteres.
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();

        // Único: dois tokens jamais colidem, e a busca na renovação é por este campo.
        builder.HasIndex(t => t.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_hash");

        // Índice parcial sobre os tokens ainda válidos. Revogar a família inteira de uma
        // sessão (quando detectamos reuso) percorre exatamente estes.
        builder.HasIndex(t => t.UserId)
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("ix_refresh_tokens_active_by_user");

        builder.HasOne<DizidoUser>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
