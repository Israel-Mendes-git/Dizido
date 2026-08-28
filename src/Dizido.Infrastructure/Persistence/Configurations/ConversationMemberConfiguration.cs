using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("conversation_members");

        // Chave composta: o banco passa a garantir sozinho que a mesma pessoa não entra
        // duas vezes na mesma conversa. É uma regra do domínio sustentada pelo schema,
        // não por um "if" que alguém pode esquecer de escrever.
        builder.HasKey(m => new { m.ConversationId, m.UserId });

        builder.Property(m => m.Role).HasConversion<int>().IsRequired();
        builder.Property(m => m.JoinedAt).IsRequired();

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índice parcial: cobre só as linhas de membros ativos.
        // A consulta mais frequente do app é "quais conversas eu participo?" — e ela nunca
        // se importa com quem já saiu. Um índice menor cabe melhor em memória e é mais rápido.
        builder.HasIndex(m => m.UserId)
            .HasFilter("\"LeftAt\" IS NULL")
            .HasDatabaseName("ix_members_active_by_user");
    }
}
