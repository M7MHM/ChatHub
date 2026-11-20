using ChatHub.Application.Common.Caching;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace ChatHub.Infrastructure.Caching;

internal sealed class CacheInvalidationInterceptor(HybridCache cache, ILogger<CacheInvalidationInterceptor> logger)
    : SaveChangesInterceptor
{
    private readonly HashSet<string> _keys = [];
    private readonly HashSet<string> _tags = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        InvalidateAsync().AsTask().GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await InvalidateAsync();
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Clear();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Clear();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Clear();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Clear();
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case UserProfile user when entry.State is EntityState.Modified or EntityState.Deleted:
                    _keys.Add(CacheKeys.UserProfile(user.Id));
                    if (entry.State == EntityState.Deleted ||
                        IsModified(entry, nameof(UserProfile.SecurityStamp), nameof(UserProfile.IsDeleted)))
                        _keys.Add(CacheKeys.SecurityStamp(user.Id));
                    break;

                case ConversationMember member when entry.State is EntityState.Added or EntityState.Deleted ||
                                                    IsModified(entry, nameof(ConversationMember.IsActive)):
                    _keys.Add(CacheKeys.Membership(member.ConversationId, member.UserId));
                    break;

                case Conversation conversation when entry.State == EntityState.Deleted ||
                                                    IsModified(entry, nameof(Conversation.IsDeleted)):
                    _tags.Add(CacheKeys.ConversationTag(conversation.Id));
                    break;
            }
        }
    }

    private static bool IsModified(EntityEntry entry, params string[] properties) =>
        entry.State == EntityState.Modified && properties.Any(property => entry.Property(property).IsModified);

    private async ValueTask InvalidateAsync()
    {
        if (_keys.Count == 0 && _tags.Count == 0) return;

        string[] keys = [.. _keys];
        string[] tags = [.. _tags];
        Clear();

        List<string>? failed = null;
        Exception? firstError = null;

        foreach (var key in keys)
        {
            try
            {
                await cache.RemoveAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                (failed ??= []).Add(key);
                firstError ??= exception;
            }
        }

        foreach (var tag in tags)
        {
            try
            {
                await cache.RemoveByTagAsync(tag, CancellationToken.None);
            }
            catch (Exception exception)
            {
                (failed ??= []).Add($"tag {tag}");
                firstError ??= exception;
            }
        }

        if (failed is not null)
            logger.LogWarning(firstError,
                "Could not invalidate {Count} cache entries ({Entries}); they expire within their TTL.",
                failed.Count, string.Join(", ", failed));
    }

    private void Clear()
    {
        _keys.Clear();
        _tags.Clear();
    }
}
