using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.CashBook;

/// <summary>
/// The kind of a cash voucher. The system types are seeded with fixed ids and never change; the others are made by the
/// Accountant. A type used by a voucher is hidden instead of deleted, so old vouchers keep its name.
/// </summary>
public class CashVoucherType : AuditableEntity
{
    public CashDirection Direction { get; set; }
    public CashCategory Category { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Whether the vouchers of this type count in the business result (profit and loss).</summary>
    public bool CountsInResult { get; set; }

    /// <summary>A seeded type: protected from edits and deletion.</summary>
    public bool IsSystem { get; set; }

    /// <summary>May be chosen on a self-made voucher; the types the system writes itself (supplier payments, wages…) are not.</summary>
    public bool Manual { get; set; }

    /// <summary>False once the type is hidden: not offered any more, still named on the vouchers that use it.</summary>
    public bool IsActive { get; set; } = true;
}
