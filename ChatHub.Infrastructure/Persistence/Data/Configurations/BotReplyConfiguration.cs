using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class BotReplyConfiguration : IEntityTypeConfiguration<BotReply>
{
    public void Configure(EntityTypeBuilder<BotReply> builder)
    {
        builder.ToTable("BotReplies");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Ignore(r => r.DomainEvents);
        builder.Property<uint>(RefreshTokenConfiguration.RowVersionProperty).IsRowVersion();

        builder.Property(r => r.TriggerText).HasMaxLength(BotReply.MaxTriggerTextLength);

        builder.HasIndex(r => new { r.ConversationId, r.RequestedAtUtc });
        builder.HasIndex(r => r.DueAtUtc).HasDatabaseName("IX_BotReplies_Pending").HasFilter("\"CompletedAtUtc\" IS NULL");

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(r => r.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
