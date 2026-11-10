using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// PURCHASE-04 read side: the sums are computed by SQL per supplier (<see cref="Figures"/>), so nothing but the sums leaves the
/// database. Only Completed receipts, Recorded returns (with or without a receipt) and Recorded payments count (BR-PURCHASE-04).
/// A receipt counts at its total less its discount, a return at its lines less its discount, a payment at its amount plus its discount.
/// </summary>
internal sealed class SupplierDebtQuery(OmtsDbContext db) : ISupplierDebtQuery
{
    public async Task<IReadOnlyDictionary<Guid, SupplierDebtSums>> SumsAsync(Guid? supplierId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var suppliers = db.Suppliers.AsNoTracking().Where(s => supplierId == null || s.Id == supplierId);
        var rows = await Figures(db, suppliers, asOf, null, null)
            .Select(f => new { f.Supplier.Id, f.Receipts, f.Returned, f.Paid, f.PaidBack, f.StandaloneReturned })
            .ToListAsync(cancellationToken);
        return rows
            .Where(r => r.Receipts != 0 || r.Returned != 0 || r.Paid != 0 || r.PaidBack != 0)
            .ToDictionary(r => r.Id, r => new SupplierDebtSums(r.Receipts, r.Returned, r.Paid, r.PaidBack, r.StandaloneReturned));
    }

    /// <summary>
    /// The one definition of the supplier money figures, as SQL per supplier so that a list can filter, sort and page on them.
    /// <see cref="SupplierFigures.Purchased"/> covers the period when one is given, up to <paramref name="asOf"/> otherwise.
    /// </summary>
    internal static IQueryable<SupplierFigures> Figures(
        OmtsDbContext db, IQueryable<Supplier> suppliers, DateOnly asOf, DateOnly? purchasedFrom, DateOnly? purchasedTo)
    {
        var lines = CompletedLines(db);
        var receipts = db.PurchaseReceipts.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed);
        var returnLines = ReturnLines(db);
        var returns = RecordedReturns(db);
        var payments = RecordedPayments(db);
        var (from, to) = (purchasedFrom ?? DateOnly.MinValue, purchasedTo ?? asOf);
        return suppliers
            .Select(s => new
            {
                Supplier = s,
                Receipts = (lines.Where(l => l.Receipt.SupplierId == s.Id && l.Receipt.ReceiptDate <= asOf).Sum(l => (decimal?)l.LineAmount) ?? 0m)
                    - (receipts.Where(r => r.SupplierId == s.Id && r.ReceiptDate <= asOf).Sum(r => (decimal?)r.Discount) ?? 0m),
                Returned = (returnLines.Where(l => l.Return.SupplierId == s.Id && l.Return.ReturnDate <= asOf).Sum(l => (decimal?)l.ReturnValue) ?? 0m)
                    - (returns.Where(r => r.SupplierId == s.Id && r.ReturnDate <= asOf).Sum(r => (decimal?)r.Discount) ?? 0m),
                StandaloneReturned = (returnLines.Where(l => l.Return.SupplierId == s.Id && l.Return.ReceiptId == null && l.Return.ReturnDate <= asOf).Sum(l => (decimal?)l.ReturnValue) ?? 0m)
                    - (returns.Where(r => r.SupplierId == s.Id && r.ReceiptId == null && r.ReturnDate <= asOf).Sum(r => (decimal?)r.Discount) ?? 0m),
                Paid = payments.Where(p => p.SupplierId == s.Id && p.Kind == SupplierPaymentKind.Payment && p.PaymentDate <= asOf).Sum(p => (decimal?)(p.Amount + p.DiscountAmount)) ?? 0m,
                PaidBack = payments.Where(p => p.SupplierId == s.Id && p.Kind == SupplierPaymentKind.PaidBack && p.PaymentDate <= asOf).Sum(p => (decimal?)p.Amount) ?? 0m,
                Purchased = (lines.Where(l => l.Receipt.SupplierId == s.Id && l.Receipt.ReceiptDate >= from && l.Receipt.ReceiptDate <= to).Sum(l => (decimal?)l.LineAmount) ?? 0m)
                    - (receipts.Where(r => r.SupplierId == s.Id && r.ReceiptDate >= from && r.ReceiptDate <= to).Sum(r => (decimal?)r.Discount) ?? 0m),
                PurchasedInPeriod = lines.Any(l => l.Receipt.SupplierId == s.Id && l.Receipt.ReceiptDate >= from && l.Receipt.ReceiptDate <= to),
            })
            .Select(f => new SupplierFigures
            {
                Supplier = f.Supplier,
                Receipts = f.Receipts,
                Returned = f.Returned,
                StandaloneReturned = f.StandaloneReturned,
                Paid = f.Paid,
                PaidBack = f.PaidBack,
                Debt = f.Receipts - f.Returned - f.Paid + f.PaidBack,
                Purchased = f.Purchased,
                PurchasedInPeriod = f.PurchasedInPeriod,
            });
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

    public async Task<IReadOnlyList<DebtMoneyBackRow>> MoneyBackAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
        await RecordedPayments()
            .Where(p => p.Kind == SupplierPaymentKind.PaidBack && supplierIds.Contains(p.SupplierId))
            .OrderBy(p => p.PaymentDate)
            .Select(p => new DebtMoneyBackRow(p.SupplierId, p.PaymentDate, p.Amount))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DebtPaymentRow>> PaymentsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
        await RecordedPayments()
            .Where(p => p.Kind == SupplierPaymentKind.Payment && supplierIds.Contains(p.SupplierId))
            .Select(p => new DebtPaymentRow(p.SupplierId, p.Id, p.Code, p.PaymentDate, p.Amount, p.DiscountAmount, p.Method, p.RecordedByAccountId, p.StatusId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DebtAllocationRow>> AllocationsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
        await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => a.Payment.StatusId == StatusIds.SupplierPayment.Recorded && a.Payment.Kind == SupplierPaymentKind.Payment
                && a.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && supplierIds.Contains(a.Payment.SupplierId))
            .Select(a => new DebtAllocationRow(a.Payment.SupplierId, a.PaymentId, a.ReceiptId, a.Amount))
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
        Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var receipts = await ReceiptRows(db.PurchaseReceipts.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed
                && r.SupplierId == supplierId && r.ReceiptDate >= from && r.ReceiptDate <= to))
            .ToListAsync(cancellationToken);
        var returns = await RecordedReturns(db)
            .Where(r => r.SupplierId == supplierId && r.ReturnDate >= from && r.ReturnDate <= to)
            .Select(r => new StatementReturnRow(r.Id, r.Code, r.Receipt!.Code, r.ReturnDate, (r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m) - r.Discount))
            .ToListAsync(cancellationToken);
        var payments = await RecordedPayments()
            .Where(p => p.SupplierId == supplierId && p.PaymentDate >= from && p.PaymentDate <= to)
            .Select(p => new StatementPaymentRow(p.Id, p.Code, p.Kind, p.PaymentDate, p.Amount, p.Method, p.BankReference, p.Note, p.DiscountAmount))
            .ToListAsync(cancellationToken);
        return (receipts, returns, payments);
    }

    private static IQueryable<PurchaseReceiptLine> CompletedLines(OmtsDbContext db) =>
        db.PurchaseReceiptLines.AsNoTracking().Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);

    private static IQueryable<PurchaseReturnLine> ReturnLines(OmtsDbContext db) =>
        db.RecordedReturnLines().Where(l => l.Return.ReceiptId == null || l.Return.Receipt!.StatusId == StatusIds.PurchaseReceipt.Completed);

    private static IQueryable<PurchaseReturn> RecordedReturns(OmtsDbContext db) =>
        db.PurchaseReturns.AsNoTracking().Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded
            && (r.ReceiptId == null || r.Receipt!.StatusId == StatusIds.PurchaseReceipt.Completed));

    private IQueryable<SupplierPayment> RecordedPayments() => RecordedPayments(db);

    private static IQueryable<SupplierPayment> RecordedPayments(OmtsDbContext db) =>
        db.SupplierPayments.AsNoTracking().Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded);

    private static IQueryable<DebtReceiptRow> ReceiptRows(IQueryable<PurchaseReceipt> receipts) =>
        receipts.OrderBy(r => r.ReceiptDate).ThenBy(r => r.Code).Select(r => new DebtReceiptRow(
            r.SupplierId, r.Id, r.Code, r.ReceiptDate,
            r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m,
            (r.Returns.Where(x => x.StatusId == StatusIds.PurchaseReturn.Recorded).SelectMany(x => x.Lines).Sum(l => (decimal?)l.ReturnValue) ?? 0m)
                - (r.Returns.Where(x => x.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(x => (decimal?)x.Discount) ?? 0m),
            r.Discount,
            r.ReceivedAt));
}

/// <summary>A supplier with its money figures; <see cref="Debt"/> is the same sum as <see cref="SupplierDebtSums.Debt"/>, in SQL.</summary>
internal sealed class SupplierFigures
{
    public required Supplier Supplier { get; init; }
    public decimal Receipts { get; init; }
    public decimal Returned { get; init; }
    public decimal StandaloneReturned { get; init; }
    public decimal Paid { get; init; }
    public decimal PaidBack { get; init; }
    public decimal Debt { get; init; }
    public decimal Purchased { get; init; }
    public bool PurchasedInPeriod { get; init; }
}
