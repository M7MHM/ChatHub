using ChatHub.Domain.Entities;
using ChatHub.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Ignore(t => t.DomainEvents);
        builder.Property<uint>(RefreshTokenConfiguration.RowVersionProperty).IsRowVersion();

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(OpaqueToken.MaxHashLength);
        builder.Property(t => t.Email).IsRequired().HasMaxLength(254);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.CreatedAtUtc });

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
