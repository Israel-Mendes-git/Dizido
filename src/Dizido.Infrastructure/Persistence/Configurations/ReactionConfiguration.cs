using Dizido.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dizido.Infrastructure.Persistence.Configurations;

internal sealed class ReactionConfiguration : IEntityTypeConfiguration<Reaction>
{
    public void Configure(EntityTypeBuilder<Reaction> builder)
    {
        builder.ToTable("reactions");

        // Chave composta, sem Id próprio. É o banco garantindo que a mesma pessoa não reage
        // duas vezes com o mesmo emoji à mesma mensagem: dois cliques rápidos, ou dois
        // aparelhos ao mesmo tempo, não conseguem criar a segunda linha. Com um Id artificial
        // seria preciso um índice único sobre estas mesmas três colunas — a chave já é ele.
        builder.HasKey(r => new { r.MessageId, r.UserId, r.Emoji });

        // A ORDEM das colunas na chave não é decorativa. O índice da chave primária serve a
        // consultas que filtram pelas colunas da esquerda para a direita, e a consulta que
        // roda a cada abertura de conversa é "as reações destas cinquenta mensagens" —
        // MessageId primeiro. Invertida, essa consulta varreria a tabela inteira.
        builder.Property(r => r.Emoji)
            .HasMaxLength(Reaction.MaxEmojiLength)
            .IsRequired();

        builder.Property(r => r.ReactedAt).IsRequired();

        // Cascade: reação é um dado sem vida própria. Se a linha da mensagem sumir do banco de
        // verdade, um polegar órfão não significa nada. Diferente de Decision, que usa Restrict
        // sobre a mensagem — lá o registro tem valor histórico e não pode ser levado junto.
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
