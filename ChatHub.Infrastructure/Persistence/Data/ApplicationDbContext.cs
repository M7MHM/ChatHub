using System.Reflection;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Common;
using ChatHub.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Infrastructure.Persistence.Data;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    ICurrentUserService currentUser,
    IPublisher publisher,
    TimeProvider timeProvider) : DbContext(options), IApplicationDbContext
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditInformation();
        var domainEvents = CollectDomainEvents();

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
            await publisher.Publish(notification, cancellationToken);
        }

        return result;
    }

    private List<IDomainEvent> CollectDomainEvents()
    {
        var events = new List<IDomainEvent>();
        foreach (var entity in ChangeTracker.Entries<BaseEntity>().Select(e => e.Entity))
        {
            if (entity.DomainEvents.Count == 0) continue;
            events.AddRange(entity.DomainEvents);
            entity.ClearDomainEvents();
        }
        return events;
    }

    private void ApplyAuditInformation()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<BaseAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    var createdBy = entry.Property(x => x.CreatedById);
                    createdBy.CurrentValue ??= userId;
                    break;

                case EntityState.Modified:
                    entry.Property(x => x.UpdatedAtUtc).CurrentValue = now;
                    var updatedBy = entry.Property(x => x.UpdatedById);
                    updatedBy.CurrentValue = userId ?? updatedBy.CurrentValue;
                    break;
            }
        }
    }
}
