using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class DecisionConfiguration : IEntityTypeConfiguration<Decision>
{
    public void Configure(EntityTypeBuilder<Decision> builder)
    {
        builder.ToTable("decisions");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Summary)
            .HasMaxLength(Decision.MaxSummaryLength)
            .IsRequired();

        builder.Property(d => d.RegisteredAt).IsRequired();

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(d => d.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a mensagem que originou a decisão não pode ser removida do banco enquanto
        // a decisão existir. O apagamento normal é suave (a linha fica), então isto só barra
        // uma remoção física — que hoje ninguém faz, e é justamente por isso que vale declarar.
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(d => d.MessageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(d => d.RegisteredById)
            .OnDelete(DeleteBehavior.Restrict);

        // Uma mensagem vira decisão uma vez só. Sem isto, dois cliques rápidos no botão
        // criariam duas decisões idênticas, e o painel mostraria a mesma coisa duas vezes.
        builder.HasIndex(d => d.MessageId)
            .IsUnique()
            .HasDatabaseName("ux_decisions_por_mensagem");

        // O painel lista por conversa, das mais recentes para as mais antigas.
        builder.HasIndex(d => new { d.ConversationId, d.Id })
            .IsDescending(false, true)
            .HasDatabaseName("ix_decisions_por_conversa");

        builder.Ignore(d => d.IsActive);
    }
}
