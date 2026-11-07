using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A kind of oysters the cooperative sells by the kg (SALE-11, BR-SALE-05). Its name is unique, discontinued products
/// included; a Discontinued product is not offered on new requests and invoices and can be resumed (GBR-01).
/// </summary>
public class Product : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;

    /// <summary>List price per kg, in whole VND, above 0. Past prices are kept only in the audit log (BR-SALE-05).</summary>
    public decimal UnitPrice { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
