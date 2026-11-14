using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    private const int MaxTokenHashLength = 128;

    /// <summary>Shadow row version. Npgsql maps a <c>uint</c> row version to PostgreSQL's <c>xmin</c> system column.</summary>
    public const string RowVersionProperty = "RowVersion";

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

        // Optimistic concurrency: two requests rotating the same token cannot both save, the second gets
        // DbUpdateConcurrencyException. xmin is a system column, so no column is created for it.
        builder.Property<uint>(RowVersionProperty).IsRowVersion();

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(MaxTokenHashLength);
        builder.Property(t => t.ReplacedByTokenHash).HasMaxLength(MaxTokenHashLength);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
