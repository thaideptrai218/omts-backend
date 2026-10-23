using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>Allowance or deduction of a pay rate (owned by <see cref="PayRuleVersion"/>); amounts are multiples of 1,000 VND.</summary>
public class PayComponent
{
    public PayComponentKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    /// <summary>Deductions are always <see cref="PayComponentBasis.PerMonth"/>.</summary>
    public PayComponentBasis Basis { get; set; }
}
