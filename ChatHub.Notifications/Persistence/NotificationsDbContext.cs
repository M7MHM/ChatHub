using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatHub.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string ConnectionStringName = "Notifications";

    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<ConversationReadMarker> ConversationReads => Set<ConversationReadMarker>();
    public DbSet<PendingDigest> PendingDigests => Set<PendingDigest>();
    public DbSet<DigestMessage> DigestMessages => Set<DigestMessage>();
    public DbSet<DigestCooldown> DigestCooldowns => Set<DigestCooldown>();
    public DbSet<SentNotification> SentNotifications => Set<SentNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recipient>(recipient =>
        {
            recipient.ToTable("Recipients");
            recipient.HasKey(r => r.UserId);
            recipient.Property(r => r.UserId).ValueGeneratedNever();
            recipient.Property(r => r.Email).HasMaxLength(Recipient.MaxEmailLength);
            recipient.Property(r => r.DisplayName).HasMaxLength(Recipient.MaxDisplayNameLength).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(message =>
        {
            message.ToTable("ProcessedMessages");
            message.HasKey(m => m.MessageId);
            message.Property(m => m.MessageId).ValueGeneratedNever();
            message.HasIndex(m => m.ProcessedAtUtc);
        });

        modelBuilder.Entity<ConversationReadMarker>(read =>
        {
            read.ToTable("ConversationReads");
            read.HasKey(r => new { r.UserId, r.ConversationId });
            read.HasIndex(r => r.ReadUpToUtc);
        });

        modelBuilder.Entity<PendingDigest>(digest =>
        {
            digest.ToTable("PendingDigests");
            digest.HasKey(d => new { d.UserId, d.ConversationId });
            digest.Property(d => d.ConversationType).HasConversion<string>().HasMaxLength(20);
            digest.Property(d => d.ConversationTitle).HasMaxLength(PendingDigest.MaxTitleLength);
            digest.Property(d => d.LastSenderName).HasMaxLength(PendingDigest.MaxSenderNameLength).IsRequired();
            digest.Property(d => d.LastPreview).HasMaxLength(PendingDigest.MaxPreviewLength);
            digest.Property(d => d.LastError).HasMaxLength(PendingDigest.MaxErrorLength);
            digest.HasIndex(d => d.DueAtUtc).HasDatabaseName("IX_PendingDigests_Due").HasFilter("\"FailedAtUtc\" IS NULL");
        });

        modelBuilder.Entity<DigestMessage>(message =>
        {
            message.ToTable("DigestMessages");
            message.HasKey(m => new { m.UserId, m.ConversationId, m.ChatMessageId });
        });

        modelBuilder.Entity<DigestCooldown>(cooldown =>
        {
            cooldown.ToTable("DigestCooldowns");
            cooldown.HasKey(c => new { c.UserId, c.ConversationId });
            cooldown.HasIndex(c => c.LastSentAtUtc);
        });

        modelBuilder.Entity<SentNotification>(sent =>
        {
            sent.ToTable("SentNotifications");
            sent.HasKey(s => s.Id);
            sent.Property(s => s.Id).ValueGeneratedNever();
            sent.Property(s => s.Channel).HasMaxLength(SentNotification.MaxChannelLength).IsRequired();
            sent.HasIndex(s => new { s.UserId, s.SentAtUtc });
            sent.HasIndex(s => s.SentAtUtc);
        });
    }
}

public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable($"ConnectionStrings__{NotificationsDbContext.ConnectionStringName}")
                       ?? "Host=localhost;Database=chathub_notifications")
            .Options);
}
