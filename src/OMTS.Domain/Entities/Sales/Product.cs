using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>E21 – Catalog product a trader orders; no price list – prices live on the sale (BR-12).</summary>
public class Product : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SalesUnit { get; set; } = "kg";
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
