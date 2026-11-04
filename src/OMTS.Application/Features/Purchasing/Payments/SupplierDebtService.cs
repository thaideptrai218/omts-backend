using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>
/// PURCHASE-04 – the supplier debt, worked out each time it is shown and never stored (BR-PURCHASE-04). The figures are summed
/// by SQL (<see cref="ISupplierDebtQuery"/>); which receipts are unpaid and the statement are <see cref="SupplierDebtLedger"/>.
/// The API checks the role: no zone, Owner and Accountant only (§I.4.4).
/// </summary>
public sealed class SupplierDebtService(
    ISupplierDebtQuery queries, ISupplierRepository suppliers, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    /// <summary>Step 1: suppliers whose debt today is not zero, the longest-standing debt first, then the largest.</summary>
    public async Task<SupplierDebtList> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / 100), Math.Clamp(pageSize, 1, 100));
        var rows = await RowsAsync(search, cancellationToken);
        var empty = rows.Count == 0 ? UserMessages.NoDataForFilters(string.IsNullOrWhiteSpace(search) ? "suppliers" : "search") : null;
        return new SupplierDebtList(
            new Paged<SupplierDebtRow>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count),
            rows.Where(r => r.Debt > 0).Sum(r => r.Debt),
            -rows.Where(r => r.Debt < 0).Sum(r => r.Debt),
            empty?.Text,
            empty?.Code);
    }

    /// <summary>Every supplier of the list, in its order (the list pages it, the export writes it all).</summary>
    public async Task<IReadOnlyList<SupplierDebtRow>> RowsAsync(string? search, CancellationToken cancellationToken)
    {
        var sums = await queries.SumsAsync(null, timeProvider.Today(), cancellationToken);
        var nonZero = sums.Where(s => s.Value.Debt != 0).Select(s => s.Key).ToList();
        if (nonZero.Count == 0)
        {
            return [];
        }

        var found = await queries.SuppliersAsync(nonZero, search?.Trim(), cancellationToken);
        var oldest = await OldestUnpaidAsync(found.Where(s => sums[s.Id].Debt > 0).ToDictionary(s => s.Id, s => sums[s.Id]), cancellationToken);
        var today = timeProvider.Today();
        return found
            .Select(s => new SupplierDebtRow(View(s), sums[s.Id].Debt, AgeDays(oldest.GetValueOrDefault(s.Id), today), oldest.GetValueOrDefault(s.Id)))
            // A debt above zero always has a date, so what has no age is what was paid in advance: last, largest first.
            .OrderByDescending(r => r.DebtAgeDays ?? -1)
            .ThenByDescending(r => r.Debt)
            .ThenBy(r => r.Supplier.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The debt today and the unpaid receipts of any supplier (PURCHASE-05 step 1).</summary>
    public async Task<Result<SupplierDebtDetail>> GetAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        if (await suppliers.GetForEditAsync(supplierId, cancellationToken) is not { } supplier)
        {
            return AppError.NotFound;
        }

        var today = timeProvider.Today();
        var sums = await SumsAsync(supplierId, today, cancellationToken);
        var unpaid = sums.Debt > 0
            ? SupplierDebtLedger.UnpaidReceipts(await queries.ReceiptsAsync([supplierId], cancellationToken), sums.Credit, today)
            : [];
        var oldest = unpaid.Count > 0 ? unpaid[0].ReceiptDate : await OldestOfAsync(supplierId, sums, cancellationToken);
        return new SupplierDebtDetail(View(supplier), sums.Debt, AgeDays(oldest, today), oldest, unpaid);
    }

    /// <summary>Step 3: the debt at the start of the period, what happened in it, the debt at its end. The period defaults to the current month.</summary>
    public async Task<Result<SupplierDebtStatement>> StatementAsync(Guid supplierId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (await suppliers.GetForEditAsync(supplierId, cancellationToken) is not { } supplier)
        {
            return AppError.NotFound;
        }

        var today = timeProvider.Today();
        var period = PurchasePeriod.Resolve(from, to, today);
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var (start, end) = period.Value;
        var opening = start == DateOnly.MinValue ? SupplierDebtSums.Zero : await SumsAsync(supplierId, start.AddDays(-1), cancellationToken);
        var (receipts, returns, payments) = await queries.StatementRowsAsync(supplierId, start, end, cancellationToken);
        var entries = SupplierDebtLedger.Statement(opening.Debt, receipts, returns, payments);
        var todaySums = await SumsAsync(supplierId, today, cancellationToken);
        return new SupplierDebtStatement(
            View(supplier), start, end, opening.Debt, entries, entries.Count > 0 ? entries[^1].DebtAfter : opening.Debt, todaySums.Debt,
            AgeDays(await OldestOfAsync(supplierId, todaySums, cancellationToken), today));
    }

    /// <summary>The debt to the supplier today; below zero when the cooperative has paid in advance.</summary>
    public async Task<decimal> DebtAsync(Guid supplierId, CancellationToken cancellationToken) =>
        (await SumsAsync(supplierId, timeProvider.Today(), cancellationToken)).Debt;

    /// <summary>The sums of the supplier today.</summary>
    public Task<SupplierDebtSums> SumsAsync(Guid supplierId, CancellationToken cancellationToken) =>
        SumsAsync(supplierId, timeProvider.Today(), cancellationToken);

    private async Task<SupplierDebtSums> SumsAsync(Guid supplierId, DateOnly asOf, CancellationToken cancellationToken) =>
        (await queries.SumsAsync(supplierId, asOf, cancellationToken)).GetValueOrDefault(supplierId) ?? SupplierDebtSums.Zero;

    /// <summary>The date the age of one supplier's debt counts from; null when the debt is not above zero.</summary>
    private async Task<DateOnly?> OldestOfAsync(Guid supplierId, SupplierDebtSums sums, CancellationToken cancellationToken) =>
        sums.Debt > 0
            ? (await OldestUnpaidAsync(new Dictionary<Guid, SupplierDebtSums> { [supplierId] = sums }, cancellationToken)).GetValueOrDefault(supplierId)
            : null;

    /// <summary>
    /// Per supplier with a debt above zero, the date its age counts from: the date received of its oldest unpaid receipt or,
    /// when no receipt is unpaid (paid back more than paid), the date of the money paid back that no payment covers.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DateOnly?>> OldestUnpaidAsync(IReadOnlyDictionary<Guid, SupplierDebtSums> owing, CancellationToken cancellationToken)
    {
        var oldest = new Dictionary<Guid, DateOnly?>();
        if (owing.Count == 0)
        {
            return oldest;
        }

        var today = timeProvider.Today();
        var receipts = (await queries.ReceiptsAsync(owing.Keys.ToList(), cancellationToken)).ToLookup(r => r.SupplierId);
        foreach (var (supplierId, sums) in owing)
        {
            var first = SupplierDebtLedger.UnpaidReceipts(receipts[supplierId], sums.Credit, today).FirstOrDefault();
            if (first is not null)
            {
                oldest[supplierId] = first.ReceiptDate;
            }
        }

        var withoutReceipt = owing.Keys.Where(id => !oldest.ContainsKey(id)).ToList();
        if (withoutReceipt.Count > 0)
        {
            var moneyBack = (await queries.MoneyBackAsync(withoutReceipt, cancellationToken)).ToLookup(m => m.SupplierId);
            foreach (var supplierId in withoutReceipt)
            {
                oldest[supplierId] = SupplierDebtLedger.OldestUncoveredMoneyBack(moneyBack[supplierId], owing[supplierId].Paid);
            }
        }

        return oldest;
    }

    private static int? AgeDays(DateOnly? oldest, DateOnly today) => oldest is { } date ? today.DayNumber - date.DayNumber : null;

    private SupplierDebtSupplier View(DebtSupplierRow s) => new(s.Id, s.Name, s.PhoneNumber, s.StatusId, statusCatalog.DisplayNameOf(s.StatusId));

    private SupplierDebtSupplier View(Supplier s) => new(s.Id, s.Name, s.PhoneNumber, s.StatusId, statusCatalog.DisplayNameOf(s.StatusId));
}
