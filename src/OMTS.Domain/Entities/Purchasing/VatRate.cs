using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// An input VAT rate of the cooperative's list ("Thuế GTGT" of the settings). <see cref="Percent"/> is null only on the system rate KCT (not subject to tax), which is not editable and not
/// deactivatable. A rate that is deactivated stays in the list, so the lines made with it still name it. A purchase line copies the name and the percent when it takes the rate, so editing
/// a rate never changes a document that exists.
/// </summary>
public class VatRate : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>0 to 100 with at most 2 decimals; null for KCT.</summary>
    public decimal? Percent { get; set; }

    /// <summary>KCT: the goods are not subject to VAT.</summary>
    public bool IsExempt { get; set; }

    /// <summary>The seeded KCT: it cannot be edited, deactivated or made the default.</summary>
    public bool IsSystem { get; set; }

    /// <summary>The rate a new supply item starts with; exactly one active rate has it.</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
