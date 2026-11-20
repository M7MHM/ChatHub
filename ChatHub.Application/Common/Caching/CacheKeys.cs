using Microsoft.Extensions.Caching.Hybrid;

namespace ChatHub.Application.Common.Caching;

public static class CacheKeys
{
    public static readonly HybridCacheEntryOptions DefaultEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    public static readonly HybridCacheEntryOptions SecurityStampEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(2),
        Flags = HybridCacheEntryFlags.DisableLocalCache
    };

    public static readonly HybridCacheEntryOptions MembershipEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public static readonly HybridCacheEntryOptions UserProfileEntryOptions = DefaultEntryOptions;

    public static string SecurityStamp(Guid userId) => $"auth:stamp:{userId}";

    public static string Membership(Guid conversationId, Guid userId) => $"conv:member:{conversationId}:{userId}";

    public static string ConversationTag(Guid conversationId) => $"conv:{conversationId}";

    public static string UserProfile(Guid userId) => $"user:profile:{userId}";
}
