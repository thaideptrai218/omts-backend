namespace OMTS.Domain.Enums;

/// <summary>The two kinds of correction of a sales invoice (BR-SALE-10).</summary>
public enum SalesInvoiceCorrectionKind
{
    /// <summary>A mistake reported by the Zone Manager: the lines, the marks, the discount, the other charges or the customer profile change.</summary>
    DataEntryError,

    /// <summary>One amount off the total after an accepted quality claim, when the trader keeps the goods; the lines do not change.</summary>
    PriceCutAfterClaim,
}
