using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// Supplier debt (INP-06) = Σ outstanding of the supplier's Completed receipts − Σ its unallocated Recorded
/// payments (kept when a receipt was cancelled). Drafts and Cancelled receipts never count.
/// </summary>
public static class SupplierDebts
{
    public static decimal Debt(IEnumerable<PurchaseReceipt> completedReceipts, decimal unallocated) =>
        completedReceipts.Sum(r => PurchaseMoney.Of(r).Outstanding) - unallocated;
}
