using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("ConversationMembers");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Ignore(m => m.DomainEvents);

        builder.Property(m => m.Role).HasConversion<int>();

        builder.HasOne(m => m.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.ConversationId, m.UserId }).IsUnique();
        builder.HasIndex(m => m.UserId);

        builder.Property(m => m.LastReadMessageId);
        builder.Property(m => m.LastReadMessageAtUtc);
        builder.Property(m => m.LastDeliveredMessageId);
        builder.Property(m => m.LastDeliveredMessageAtUtc);
    }
}
