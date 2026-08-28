using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Body)
            .HasMaxLength(Message.MaxBodyLength)
            .IsRequired();

        builder.Property(m => m.ClientMessageId).IsRequired();
        builder.Property(m => m.SentAt).IsRequired();

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(m => m.SenderId)
            // Restrict: apagar um usuário não pode arrastar as mensagens dele junto, senão
            // some metade do histórico do grupo. Conta desativada mantém as mensagens.
            .OnDelete(DeleteBehavior.Restrict);

        // Auto-relacionamento para respostas. Se a mensagem citada for apagada de vez,
        // a resposta continua existindo com ReplyToMessageId nulo, em vez de sumir junto.
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(m => m.ReplyToMessageId)
            .OnDelete(DeleteBehavior.SetNull);

        // Índice que sustenta a paginação por cursor:
        //   WHERE ConversationId = @id AND Id < @cursor ORDER BY Id DESC LIMIT 50
        // Como o Id é UUIDv7 (ordenável no tempo), este único índice serve tanto para
        // filtrar por conversa quanto para ordenar por recência — sem sort em memória.
        builder.HasIndex(m => new { m.ConversationId, m.Id })
            .IsDescending(false, true)
            .HasDatabaseName("ix_messages_by_conversation");

        // Deduplicação: o mesmo remetente não grava duas mensagens com o mesmo
        // ClientMessageId. É o que torna o reenvio seguro quando a rede cai.
        builder.HasIndex(m => new { m.SenderId, m.ClientMessageId })
            .IsUnique()
            .HasDatabaseName("ux_messages_dedup");

        builder.Property(m => m.Kind).HasConversion<int>().IsRequired();
        builder.Property(m => m.SystemEvent).HasConversion<int?>();

        builder.Ignore(m => m.IsDeleted);
        builder.Ignore(m => m.IsEdited);
        builder.Ignore(m => m.IsSystem);
    }
}
