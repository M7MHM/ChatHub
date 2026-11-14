using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<UserProfile> UserProfiles { get; }
    DbSet<UserBlock> UserBlocks { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<ConversationMember> ConversationMembers { get; }
    DbSet<Message> Messages { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
