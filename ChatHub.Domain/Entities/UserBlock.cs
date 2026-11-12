using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class UserBlock : BaseEntity
{
    private UserBlock() { }

    public Guid BlockerId { get; private set; }
    public UserProfile Blocker { get; private set; } = null!;
    public Guid BlockedId { get; private set; }
    public UserProfile Blocked { get; private set; } = null!;
    public DateTime BlockedAtUtc { get; private set; } = DateTime.UtcNow;

    public static UserBlock Create(Guid blockerId, Guid blockedId)
    {
        if (blockerId == Guid.Empty || blockedId == Guid.Empty)
            throw new DomainRuleViolationException("Blocker and blocked user ids are required.");
        if (blockerId == blockedId)
            throw new DomainRuleViolationException("A user cannot block themselves.");

        return new UserBlock { BlockerId = blockerId, BlockedId = blockedId };
    }
}
