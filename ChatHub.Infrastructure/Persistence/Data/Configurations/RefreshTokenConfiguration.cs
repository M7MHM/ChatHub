using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    private const int MaxTokenHashLength = 128;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);

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
