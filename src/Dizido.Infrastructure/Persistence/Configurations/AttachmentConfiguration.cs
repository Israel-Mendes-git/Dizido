using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.FileName)
            .HasMaxLength(Attachment.MaxFileNameLength)
            .IsRequired();

        builder.Property(a => a.ContentType)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.StorageKey)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.ThumbnailKey).HasMaxLength(200);

        builder.Property(a => a.SizeBytes).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.Property(a => a.Kind).HasConversion<int>().IsRequired();
        builder.Property(a => a.Status).HasConversion<int>().IsRequired();

        // Dois objetos não podem apontar para o mesmo lugar no bucket. Hoje a chave é derivada
        // de um UUIDv7 e a colisão é impossível na prática; o índice está aqui porque o dia em
        // que alguém mudar a forma de montar a chave, o banco recusa em vez de sobrescrever
        // o arquivo de outra pessoa em silêncio.
        builder.HasIndex(a => a.StorageKey)
            .IsUnique()
            .HasDatabaseName("ux_attachments_storage_key");

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(a => a.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(a => a.UploadedById)
            .OnDelete(DeleteBehavior.Restrict);

        // Sustenta a faxina de anexos abandonados: quem pediu a URL e nunca subiu os bytes
        // deixa uma linha Pending para sempre. Sem este índice, varrer a tabela inteira em
        // busca deles fica caro conforme o histórico cresce.
        builder.HasIndex(a => new { a.Status, a.CreatedAt })
            .HasDatabaseName("ix_attachments_pendentes");

        builder.Ignore(a => a.IsReady);
    }
}
