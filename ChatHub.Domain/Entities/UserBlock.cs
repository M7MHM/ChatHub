using ChatHub.Domain.Common;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.Entities;

public class UserBlock : BaseAuditableEntity
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
    public void Unblock()
    {
        if (IsDeleted) return;
        IsDeleted = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }
    public void Reblock()
    {
        if (!IsDeleted) return;
        IsDeleted = false;
        BlockedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
