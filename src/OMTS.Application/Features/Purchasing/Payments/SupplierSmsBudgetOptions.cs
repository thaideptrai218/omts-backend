namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>
/// The daily budget of the SMS of supplier payment transactions (MSG-PURCHASE-04, mandatory): there is no hard cap (PM decision of 17/11/2026), but the Owner is told once a day when the
/// transactions of the day reach <see cref="AlertPercent"/> % of the budget. A budget of 0 (the default) switches the alert off. Section <c>Sms:SupplierTransactionBudget</c>.
/// </summary>
public sealed class SupplierSmsBudgetOptions
{
    public const string SectionName = "Sms:SupplierTransactionBudget";

    /// <summary>The share of the budget at which the Owner is alerted.</summary>
    public const int AlertPercent = 80;

    /// <summary>How many supplier transactions (one SMS each) a day the cooperative plans for; 0 = no budget.</summary>
    public int DailyBudget { get; set; }
}
