using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class MessageReceiptConfiguration : IEntityTypeConfiguration<MessageReceipt>
{
    public void Configure(EntityTypeBuilder<MessageReceipt> builder)
    {
        builder.ToTable("MessageReceipts");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Ignore(r => r.DomainEvents);

        builder.Property(r => r.Status).HasConversion<int>();

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.MessageId, r.UserId }).IsUnique();
        builder.HasIndex(r => r.UserId);
    }
}
