using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// PURCHASE-04 read side: one grouped SQL sum per kind of figure, merged per supplier, so nothing but the sums leaves the
/// database. Only Completed receipts (and so only their returns) and Recorded payments count (BR-PURCHASE-04).
/// </summary>
internal sealed class SupplierDebtQuery(OmtsDbContext db) : ISupplierDebtQuery
{
    public async Task<IReadOnlyDictionary<Guid, SupplierDebtSums>> SumsAsync(Guid? supplierId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var receipts = await CompletedLines()
            .Where(l => l.Receipt.ReceiptDate <= asOf && (supplierId == null || l.Receipt.SupplierId == supplierId))
            .GroupBy(l => l.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.LineAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var returned = await ReturnLines()
            .Where(l => l.Return.ReturnDate <= asOf && (supplierId == null || l.Return.Receipt.SupplierId == supplierId))
            .GroupBy(l => l.Return.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(l => l.ReturnValue) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var refunded = await Returns()
            .Where(r => r.ReturnDate <= asOf && (supplierId == null || r.Receipt.SupplierId == supplierId))
            .GroupBy(r => r.Receipt.SupplierId).Select(g => new { g.Key, Amount = g.Sum(r => r.RefundAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var payments = await RecordedPayments()
            .Where(p => p.PaymentDate <= asOf && (supplierId == null || p.SupplierId == supplierId))
            .GroupBy(p => new { p.SupplierId, p.Kind }).Select(g => new { g.Key.SupplierId, g.Key.Kind, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var paid = payments.Where(p => p.Kind == SupplierPaymentKind.Payment).ToDictionary(p => p.SupplierId, p => p.Amount);
        var paidBack = payments.Where(p => p.Kind == SupplierPaymentKind.PaidBack).ToDictionary(p => p.SupplierId, p => p.Amount);
        return receipts.Keys.Union(returned.Keys).Union(refunded.Keys).Union(paid.Keys).Union(paidBack.Keys)
            .ToDictionary(id => id, id => new SupplierDebtSums(
                receipts.GetValueOrDefault(id), returned.GetValueOrDefault(id), refunded.GetValueOrDefault(id), paid.GetValueOrDefault(id), paidBack.GetValueOrDefault(id)));
    }

    public async Task<IReadOnlyList<DebtSupplierRow>> SuppliersAsync(IReadOnlyCollection<Guid> supplierIds, string? search, CancellationToken cancellationToken)
    {
        var query = db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(s => s.Name.Contains(search) || s.PhoneNumber.Contains(search));
        }

        return await query.Select(s => new DebtSupplierRow(s.Id, s.Name, s.PhoneNumber, s.StatusId)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DebtReceiptRow>> ReceiptsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
        await ReceiptRows(db.PurchaseReceipts.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed && supplierIds.Contains(r.SupplierId)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DebtMoneyBackRow>> MoneyBackAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        var paidBack = await RecordedPayments()
            .Where(p => p.Kind == SupplierPaymentKind.PaidBack && supplierIds.Contains(p.SupplierId))
            .Select(p => new DebtMoneyBackRow(p.SupplierId, p.PaymentDate, p.Amount))
            .ToListAsync(cancellationToken);
        var refunds = await Returns()
            .Where(r => r.RefundAmount > 0 && supplierIds.Contains(r.Receipt.SupplierId))
            .Select(r => new DebtMoneyBackRow(r.Receipt.SupplierId, r.ReturnDate, r.RefundAmount))
            .ToListAsync(cancellationToken);
        return paidBack.Concat(refunds).OrderBy(m => m.Date).ToList();
    }

    public async Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
        Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var receipts = await ReceiptRows(db.PurchaseReceipts.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed
                && r.SupplierId == supplierId && r.ReceiptDate >= from && r.ReceiptDate <= to))
            .ToListAsync(cancellationToken);
        var returns = await Returns()
            .Where(r => r.Receipt.SupplierId == supplierId && r.ReturnDate >= from && r.ReturnDate <= to)
            .Select(r => new StatementReturnRow(r.Id, r.Code, r.Receipt.Code, r.ReturnDate, r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m, r.RefundAmount))
            .ToListAsync(cancellationToken);
        var payments = await RecordedPayments()
            .Where(p => p.SupplierId == supplierId && p.PaymentDate >= from && p.PaymentDate <= to)
            .Select(p => new StatementPaymentRow(p.Id, p.Code, p.Kind, p.PaymentDate, p.Amount, p.Method, p.BankReference, p.Note))
            .ToListAsync(cancellationToken);
        return (receipts, returns, payments);
    }

    private IQueryable<PurchaseReceiptLine> CompletedLines() =>
        db.PurchaseReceiptLines.AsNoTracking().Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);

    private IQueryable<PurchaseReturn> Returns() =>
        db.PurchaseReturns.AsNoTracking().Where(r => r.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);

    private IQueryable<PurchaseReturnLine> ReturnLines() =>
        db.PurchaseReturnLines.AsNoTracking().Where(l => l.Return.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);

    private IQueryable<SupplierPayment> RecordedPayments() =>
        db.SupplierPayments.AsNoTracking().Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded);

    private static IQueryable<DebtReceiptRow> ReceiptRows(IQueryable<PurchaseReceipt> receipts) =>
        receipts.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code).Select(r => new DebtReceiptRow(
            r.SupplierId, r.Id, r.Code, r.ReceiptDate,
            r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m,
            r.Returns.SelectMany(x => x.Lines).Sum(l => (decimal?)l.ReturnValue) ?? 0m));
}
