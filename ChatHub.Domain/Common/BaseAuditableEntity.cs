namespace ChatHub.Domain.Common;

public abstract class BaseAuditableEntity : BaseEntity
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedById { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedById { get; set; }
    public bool IsDeleted { get; set; } = false;
}
