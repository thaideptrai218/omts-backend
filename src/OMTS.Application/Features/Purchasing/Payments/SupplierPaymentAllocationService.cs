using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>A payment read as covering one receipt; <paramref name="Applied"/> is the part of it that falls on this receipt.</summary>
public sealed record ReceiptPaymentView(
    Guid Id, string Code, DateOnly PaymentDate, AccountRef RecordedBy, PaymentMethod Method, int StatusId, string Status, decimal Applied);

/// <summary>
/// What the supplier ledger says each receipt has been paid (<see cref="SupplierDebtLedger.AllocatePayments"/>): the parts of
/// payments set against a receipt cover it first, the rest of the payments the oldest receipts. This reading is never stored;
/// only the parts the Accountant set are.
/// </summary>
public sealed class SupplierPaymentAllocationService(ISupplierDebtQuery queries, IAccountRepository accounts, IStatusCatalog statusCatalog)
{
    /// <summary>Per Completed receipt of the suppliers, what payments cover of it; a receipt nothing covers has no entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> PaidByReceiptAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        if (supplierIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var reading = await ReadAsync(supplierIds, cancellationToken);
        return reading.Allocations.GroupBy(a => a.ReceiptId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
    }

    /// <summary>The payments that cover the receipt, oldest first.</summary>
    public async Task<IReadOnlyList<ReceiptPaymentView>> PaymentsOfAsync(Guid receiptId, Guid supplierId, CancellationToken cancellationToken)
    {
        var reading = await ReadAsync([supplierId], cancellationToken);
        var applied = reading.Allocations.Where(a => a.ReceiptId == receiptId).ToDictionary(a => a.PaymentId, a => a.Amount);
        var rows = reading.Payments.Where(p => applied.ContainsKey(p.Id)).OrderBy(p => p.PaymentDate).ThenBy(p => p.Code, StringComparer.Ordinal).ToList();
        var names = await accounts.GetDisplayNamesAsync(rows.Select(p => p.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        return rows.Select(p => new ReceiptPaymentView(
            p.Id, p.Code, p.PaymentDate, new AccountRef(p.RecordedByAccountId, names.GetValueOrDefault(p.RecordedByAccountId) ?? string.Empty),
            p.Method, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), applied[p.Id])).ToList();
    }

    /// <summary>
    /// Per receipt, what the other payments cover of it now (<c>paidBefore</c>) and what is left on it after every payment
    /// (<c>remaining</c>, read as <see cref="SupplierDebtLedger.UnpaidReceipts"/> does). A receipt no longer Completed has neither.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, (decimal PaidByOthers, decimal Remaining)>> ReceiptStandingAsync(
        Guid supplierId, Guid paymentId, DateOnly today, CancellationToken cancellationToken)
    {
        var reading = await ReadAsync([supplierId], cancellationToken);
        var credit = (await queries.SumsAsync(supplierId, today, cancellationToken)).GetValueOrDefault(supplierId)?.Credit ?? 0;
        var unpaid = SupplierDebtLedger.UnpaidReceipts(reading.Receipts, credit, today, reading.Named).ToDictionary(u => u.Id, u => u.Remaining);
        var others = reading.Allocations.Where(a => a.PaymentId != paymentId).GroupBy(a => a.ReceiptId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        return reading.Receipts.ToDictionary(r => r.Id, r => (PaidByOthers: others.GetValueOrDefault(r.Id), Remaining: unpaid.GetValueOrDefault(r.Id)));
    }

    private sealed record Reading(
        IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<DebtPaymentRow> Payments, IReadOnlyList<DebtAllocationRow> Named, IReadOnlyList<PaymentAllocation> Allocations);

    private async Task<Reading> ReadAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken)
    {
        var receipts = await queries.ReceiptsAsync(supplierIds, cancellationToken);
        var payments = await queries.PaymentsAsync(supplierIds, cancellationToken);
        var named = await queries.AllocationsAsync(supplierIds, cancellationToken);
        var receiptsOf = receipts.ToLookup(r => r.SupplierId);
        var paymentsOf = payments.ToLookup(p => p.SupplierId);
        var namedOf = named.ToLookup(a => a.SupplierId);
        var moneyBack = (await queries.MoneyBackAsync(supplierIds, cancellationToken)).ToLookup(m => m.SupplierId);
        var allocations = supplierIds
            .SelectMany(id => SupplierDebtLedger.AllocatePayments(receiptsOf[id], paymentsOf[id], moneyBack[id].Sum(m => m.Amount), namedOf[id]))
            .ToList();
        return new Reading(receipts, payments, named, allocations);
    }
}
