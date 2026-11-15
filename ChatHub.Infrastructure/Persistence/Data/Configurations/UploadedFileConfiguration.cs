using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class UploadedFileConfiguration : IEntityTypeConfiguration<UploadedFile>
{
    public void Configure(EntityTypeBuilder<UploadedFile> builder)
    {
        builder.ToTable("UploadedFiles");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Ignore(f => f.DomainEvents);

        builder.Property<uint>(RefreshTokenConfiguration.RowVersionProperty).IsRowVersion();

        builder.Property(f => f.Purpose).HasConversion<int>();
        builder.Property(f => f.StorageKey).IsRequired().HasMaxLength(UploadedFile.MaxStorageKeyLength);
        builder.Property(f => f.FileName).IsRequired().HasMaxLength(UploadedFile.MaxFileNameLength);
        builder.Property(f => f.ContentType).IsRequired().HasMaxLength(UploadedFile.MaxContentTypeLength);

        builder.HasIndex(f => f.OwnerId);
        builder.HasIndex(f => new { f.IsDeleted, f.AttachedAtUtc });

        builder.HasOne<UserProfile>()
            .WithMany()
            .HasForeignKey(f => f.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
