using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        // Enum gravado como int. Alternativa seria string (legível ao inspecionar o banco),
        // mas int é estável: renomear ConversationType.Direct no C# não invalida os dados.
        builder.Property(c => c.Type)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(c => c.Title).HasMaxLength(Conversation.MaxTitleLength);
        builder.Property(c => c.AvatarUrl).HasMaxLength(500);
        builder.Property(c => c.CreatedById).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.LastMessageAt).IsRequired();

        // A coleção é exposta como IReadOnlyList e alimentada pelo campo _members.
        // Este trecho diz ao EF: "escreva direto no campo, ignore a propriedade".
        // Sem isso o EF tentaria usar o setter da propriedade — que não existe.
        builder.Metadata
            .FindNavigation(nameof(Conversation.Members))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Members)
            .WithOne()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // ActiveMembers é IEnumerable calculado com LINQ sobre a lista — não é coluna.
        builder.Ignore(c => c.ActiveMembers);
    }
}
