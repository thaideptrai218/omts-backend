namespace OMTS.Domain.Enums;

/// <summary>What a supplier payment records (SRS 27/10 §I.3.2, PURCHASE-05).</summary>
public enum SupplierPaymentKind
{
    /// <summary>Money the Accountant paid to the supplier; it lowers the supplier debt.</summary>
    Payment,

    /// <summary>Money the supplier paid back (PURCHASE-05.2); it raises the supplier debt.</summary>
    PaidBack,
}
