using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Payments;

public sealed record SupplierDebtRow(
    Guid SupplierId, string SupplierName, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Unallocated, decimal Outstanding);

public sealed record ReceiptDebtRow(
    Guid ReceiptId, string Code, DateOnly ReceiptDate, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Outstanding);

public sealed record SupplierDebtDetail(SupplierDebtRow Totals, IReadOnlyList<ReceiptDebtRow> Receipts);

/// <summary>INP-06 supplier debt: per supplier and per receipt, from Completed receipts and unallocated payments.</summary>
public sealed class SupplierDebtService(IPurchaseReceiptRepository receipts, ISupplierPaymentRepository payments, ISupplierRepository suppliers)
{
    public async Task<IReadOnlyList<SupplierDebtRow>> ListAsync(CancellationToken cancellationToken)
    {
        var completed = await receipts.CompletedOfSuppliersAsync(null, cancellationToken);
        var unallocated = await payments.UnallocatedAsync(null, cancellationToken);
        var rows = new List<SupplierDebtRow>();
        foreach (var supplierId in completed.Select(r => r.SupplierId).Union(unallocated.Keys).Distinct())
        {
            var name = completed.FirstOrDefault(r => r.SupplierId == supplierId)?.Supplier?.Name
                ?? (await suppliers.GetForEditAsync(supplierId, cancellationToken))?.Name ?? string.Empty;
            rows.Add(Totals(supplierId, name, completed.Where(r => r.SupplierId == supplierId), unallocated.GetValueOrDefault(supplierId)));
        }

        return rows.OrderByDescending(r => r.Outstanding).ThenBy(r => r.SupplierName).ToList();
    }

    public async Task<Result<SupplierDebtDetail>> GetAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetForEditAsync(supplierId, cancellationToken);
        if (supplier is null)
        {
            return AppError.NotFound;
        }

        var completed = await receipts.CompletedOfSuppliersAsync([supplierId], cancellationToken);
        var unallocated = (await payments.UnallocatedAsync([supplierId], cancellationToken)).GetValueOrDefault(supplierId);
        return new SupplierDebtDetail(
            Totals(supplierId, supplier.Name, completed, unallocated),
            completed.Select(r =>
            {
                var f = PurchaseMoney.Of(r);
                return new ReceiptDebtRow(r.Id, r.Code, r.ReceiptDate, f.Payable, f.Returned, f.Paid, f.Refunded, f.Outstanding);
            }).ToList());
    }

    private static SupplierDebtRow Totals(Guid supplierId, string name, IEnumerable<PurchaseReceipt> completed, decimal unallocated)
    {
        var figures = completed.Select(PurchaseMoney.Of).ToList();
        return new SupplierDebtRow(
            supplierId, name, figures.Sum(f => f.Payable), figures.Sum(f => f.Returned), figures.Sum(f => f.Paid), figures.Sum(f => f.Refunded),
            unallocated, figures.Sum(f => f.Outstanding) - unallocated);
    }
}
