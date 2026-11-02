using OMTS.Application.Common.Models.Persistence;
using OMTS.Application.Features.Purchasing.Payments.Dtos;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>INP-06 supplier debt: per supplier and per receipt, from Completed receipts and unallocated payments (summed in SQL).</summary>
public sealed class SupplierDebtService(IPurchaseReceiptRepository receipts, ISupplierPaymentRepository payments, ISupplierRepository suppliers)
{
    public async Task<IReadOnlyList<SupplierDebtRow>> ListAsync(CancellationToken cancellationToken)
    {
        var figures = (await receipts.SupplierDebtFiguresAsync(null, cancellationToken)).ToDictionary(f => f.SupplierId);
        var unallocated = await payments.UnallocatedAsync(null, cancellationToken);
        var rows = new List<SupplierDebtRow>();
        foreach (var supplierId in figures.Keys.Union(unallocated.Keys))
        {
            var name = figures.TryGetValue(supplierId, out var own)
                ? own.SupplierName
                : (await suppliers.GetForEditAsync(supplierId, cancellationToken))?.Name ?? string.Empty;
            rows.Add(Totals(supplierId, name, own, unallocated.GetValueOrDefault(supplierId)));
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

        var figures = (await receipts.SupplierDebtFiguresAsync([supplierId], cancellationToken)).FirstOrDefault();
        var completed = await receipts.CompletedFiguresOfSupplierAsync(supplierId, cancellationToken);
        var unallocated = (await payments.UnallocatedAsync([supplierId], cancellationToken)).GetValueOrDefault(supplierId);
        return new SupplierDebtDetail(
            Totals(supplierId, supplier.Name, figures, unallocated),
            completed.Select(r => new ReceiptDebtRow(r.Id, r.Code, r.ReceiptDate, r.Payable, r.Returned, r.Paid, r.Refunded, r.Outstanding)).ToList());
    }

    private static SupplierDebtRow Totals(Guid supplierId, string name, SupplierDebtFigures? figures, decimal unallocated) => new(
        supplierId, name, figures?.Payable ?? 0, figures?.Returned ?? 0, figures?.Paid ?? 0, figures?.Refunded ?? 0,
        unallocated, (figures?.Outstanding ?? 0) - unallocated);
}
