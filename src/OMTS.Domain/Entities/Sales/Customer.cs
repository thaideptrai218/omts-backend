using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// E22 – Buyer (trader). May exist offline before having an account; an account links to at most one
/// customer and vice versa (BR-34). Cooperative-wide data.
/// </summary>
public class Customer : AuditableEntity, IHasStatus
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
    public string? OtherContacts { get; set; }

    /// <summary>Zalo account; linked to at most one customer.</summary>
    public string? ZaloAccountId { get; set; }

    public string? DeliveryAddress { get; set; }
    public string? ReceivingContact { get; set; }

    /// <summary>Set when this profile was merged into another one (UC42).</summary>
    public Guid? MergedIntoCustomerId { get; set; }
    public Customer? MergedIntoCustomer { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
