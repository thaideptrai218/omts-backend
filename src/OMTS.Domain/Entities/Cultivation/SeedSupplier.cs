using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>E14 – Seed supplier reused across intakes. Cooperative-wide data (All zones roles only).</summary>
public class SeedSupplier : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? OtherContacts { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<SeedReceiptLot> ReceiptLots { get; set; } = [];
}
