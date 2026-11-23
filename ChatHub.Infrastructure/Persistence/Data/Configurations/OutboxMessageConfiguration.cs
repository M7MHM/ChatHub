using ChatHub.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Type).HasMaxLength(OutboxMessage.MaxTypeLength).IsRequired();
        builder.Property(x => x.RoutingKey).HasMaxLength(OutboxMessage.MaxRoutingKeyLength).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);

        builder.HasIndex(x => x.OccurredAtUtc)
            .HasDatabaseName("IX_OutboxMessages_Pending")
            .HasFilter("\"ProcessedAtUtc\" IS NULL");
        builder.HasIndex(x => x.ProcessedAtUtc)
            .HasDatabaseName("IX_OutboxMessages_Processed")
            .HasFilter("\"ProcessedAtUtc\" IS NOT NULL");
    }
}
