using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("UserProfiles");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Ignore(u => u.DomainEvents);

        builder.Property(u => u.PhoneNumber)
            .HasConversion(new ValueConverter<PhoneNumber?, string?>(p => p!.FullNumber, s => PhoneNumber.Create(s!)))
            .HasMaxLength(20);

        builder.Property(u => u.Email)
            .HasConversion(new ValueConverter<Email?, string?>(e => e!.Value, s => Email.Create(s!)))
            .HasMaxLength(254);

        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(UserProfile.MaxDisplayNameLength);
        builder.Property(u => u.AvatarUrl).HasMaxLength(2048);
        builder.Property(u => u.Bio).HasMaxLength(UserProfile.MaxBioLength);

        builder.HasIndex(u => u.PhoneNumber).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasQueryFilter(u => !u.IsDeleted);
    }
}
