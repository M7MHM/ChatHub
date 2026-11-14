using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatHub.Infrastructure.Persistence.Data.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Ignore(m => m.DomainEvents);

        builder.Property(m => m.Type).HasConversion<int>();
        builder.Property(m => m.Content).HasMaxLength(Message.MaxContentLength);

        builder.ComplexProperty(m => m.Attachment, a =>
        {
            a.IsRequired(false);
            a.Property(x => x.FileUrl).HasColumnName("AttachmentUrl").HasMaxLength(2048);
            a.Property(x => x.FileName).HasColumnName("AttachmentFileName").HasMaxLength(255);
            a.Property(x => x.ContentType).HasColumnName("AttachmentContentType").HasMaxLength(255);
            a.Property(x => x.FileSizeBytes).HasColumnName("AttachmentSizeBytes");
        });

        builder.HasOne(m => m.Sender)
            .WithMany()
            .HasForeignKey(m => m.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.ParentMessage)
            .WithMany()
            .HasForeignKey(m => m.ParentMessageId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.ConversationId, m.CreatedAtUtc, m.Id });
        builder.HasIndex(m => m.SenderId);

    }
}
