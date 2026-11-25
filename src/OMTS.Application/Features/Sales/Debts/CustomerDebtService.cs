using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Sales.Debts;

/// <summary>
/// DEBT-03 – the customer debts, worked out each time they are shown and never stored (BR-DEBT-02). The debts are summed by SQL (<see cref="ICustomerDebtQuery"/>,
/// the figure every other screen shows); the unpaid invoices and the age come from <see cref="CustomerDebtAllocator"/> and the statement from
/// <see cref="CustomerStatementLedger"/>. Read only. The API checks the role: no zone, Owner and Accountant (§I.4.4, BR-FARM-08).
/// </summary>
/// <summary>The rows of the DEBT-03 list (after the search) and the cooperative totals (every profile, whatever the search).</summary>
public sealed record CustomerDebtRows(IReadOnlyList<CustomerDebtRow> Rows, decimal TotalReceivables, decimal TotalOwedBack);

public sealed class CustomerDebtService(ICustomerDebtQuery queries, CustomerDebtReader debts, SalesInvoiceCorrectionLog corrections, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    /// <summary>Step 1: profiles whose debt today is not 0, the longest-standing debt first, then the largest.</summary>
    public async Task<CustomerDebtList> ListAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (rows, totalReceivables, totalOwedBack) = await RowsAsync(search, cancellationToken);
        var empty = rows.Count == 0
            ? string.IsNullOrWhiteSpace(search) ? UserMessages.NothingToShowYet : UserMessages.NoDataForFilters("search")
            : null;
        return new CustomerDebtList(
            new Paged<CustomerDebtRow>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count),
            totalReceivables,
            totalOwedBack,
            await queries.AwaitingConfirmationCountAsync(cancellationToken),
            empty?.Text,
            empty?.Code);
    }

    /// <summary>
    /// Every profile of the list, in its order (the list pages it, the export writes it all), and the cooperative's totals (BR-FARM-10, BR-DEBT-02): the totals are of every profile,
    /// whatever the search. A fixed number of queries, whatever the number of profiles: the debts and the age of each debt come from the database, and no ledger is read except of a
    /// profile that holds an advance reserved for an open order.
    /// </summary>
    public async Task<CustomerDebtRows> RowsAsync(string? search, CancellationToken cancellationToken) =>
        await RowsAsync(await DebtOfEveryProfileAsync(cancellationToken), search, cancellationToken);

    /// <summary>The rows of <see cref="RowsAsync(string?, CancellationToken)"/> for debts the caller has read already.</summary>
    public async Task<CustomerDebtRows> RowsAsync(IReadOnlyDictionary<Guid, decimal> debtOf, string? search, CancellationToken cancellationToken)
    {
        var (totalReceivables, totalOwedBack) = TotalsOf(debtOf);
        var nonZero = debtOf.Where(d => d.Value != 0).Select(d => d.Key).ToList();
        if (nonZero.Count == 0)
        {
            return new CustomerDebtRows([], totalReceivables, totalOwedBack);
        }

        var found = await queries.CustomersAsync(nonZero, search, cancellationToken);
        var ages = await debts.AgesAsync(found.ToDictionary(c => c.Id, c => debtOf[c.Id]), cancellationToken);
        var rows = found
            .Select(c =>
            {
                var age = ages[c.Id];
                // A debt of 0 or below has no age, even while advances reserved for orders leave invoices unpaid.
                var owes = debtOf[c.Id] > 0;
                return new CustomerDebtRow(View(c), debtOf[c.Id], owes ? age.DebtAgeDays : null, owes ? age.OldestUnpaidDate : null, age.ReservedTotal);
            })
            // Money owed back has no age: last, largest first.
            .OrderByDescending(r => r.DebtAgeDays ?? -1)
            .ThenBy(r => r.Debt < 0)
            .ThenByDescending(r => Math.Abs(r.Debt))
            .ThenBy(r => r.Customer.Name, StringComparer.Ordinal)
            .ToList();
        return new CustomerDebtRows(rows, totalReceivables, totalOwedBack);
    }

    /// <summary>The cooperative's totals alone (BR-FARM-10, BR-DEBT-02): the debts above zero, and the money owed back to customers shown apart. The operations dashboard (FARM-06) reads them here.</summary>
    public async Task<(decimal Receivables, decimal OwedBack)> TotalsAsync(CancellationToken cancellationToken) =>
        TotalsOf(await DebtOfEveryProfileAsync(cancellationToken));

    /// <summary>The debt of every profile with an invoice or a payment: one read, for the callers that need the debts for several purposes.</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> DebtOfEveryProfileAsync(CancellationToken cancellationToken) =>
        (await queries.AllFiguresAsync(cancellationToken)).ToDictionary(f => f.Key, f => CustomerDebtMath.Debt(f.Value));

    /// <summary>The totals of <see cref="TotalsAsync"/> for debts read already.</summary>
    public static (decimal Receivables, decimal OwedBack) TotalsOf(IReadOnlyDictionary<Guid, decimal> debtOf) =>
        (debtOf.Values.Sum(CustomerDebtMath.Receivable), debtOf.Values.Sum(CustomerDebtMath.OwedBack));

    /// <summary>Step 3: the debt at the start of the period, what happened in it, the debt at its end, and the invoices unpaid today. The period defaults to the current month.</summary>
    public async Task<Result<CustomerDebtStatement>> StatementAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if ((await queries.CustomersAsync([customerId], null, cancellationToken)).FirstOrDefault() is not { } customer)
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
        // The staff statement: payments with who recorded, confirmed and collected them, their note and cancel reason (BR-DEBT-04). Only the rows of the period are read; what came
        // before it is one sum, the debt at its start.
        var opening = await queries.DebtBeforeAsync(customerId, start, cancellationToken);
        var ledger = await queries.StatementLedgerAsync(customerId, start, end, cancellationToken);
        // BR-DEBT-04: under each invoice of the period, its corrections, also one made after the period (the price cuts are entries of their own).
        var invoicesInPeriod = ledger.Invoices.Where(i => i.InvoiceDate >= start && i.InvoiceDate <= end).Select(i => i.Id).ToList();
        var lines = CustomerStatementLedger.Build(
            ledger.Invoices, ledger.Payments, start, end, ledger.Adjustments, await corrections.OfAsync(invoicesInPeriod, cancellationToken),
            await queries.ReturnsOfAsync(customerId, start, end, cancellationToken), opening);
        var now = await debts.SnapshotAsync(customerId, cancellationToken);
        return new CustomerDebtStatement(
            View(customer), start, end, lines.OpeningDebt, lines.Entries, lines.ClosingDebt, now.Debt, now.DebtAgeDays, now.UnpaidInvoices, now.ReservedAdvances);
    }

    /// <summary>
    /// The debt tab of one profile: its debt now, the unpaid invoices and its whole history (or a period, or some kinds) newest first. The debt after each entry is worked out
    /// over the whole history, so a filter never changes it. The caller needs CUSTOMER_DEBTS View (API).
    /// </summary>
    public async Task<Result<CustomerDebtTab>> TabAsync(Guid customerId, CustomerDebtTabQuery query, CancellationToken cancellationToken)
    {
        if ((await queries.CustomersAsync([customerId], null, cancellationToken)).FirstOrDefault() is not { } customer)
        {
            return AppError.NotFound;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        // The debt after each entry is that of the whole history: the debt before the period is one sum, then the rows of the period (no later row changes an earlier entry).
        var (from, to) = (query.From ?? DateOnly.MinValue, query.To ?? DateOnly.MaxValue);
        var opening = query.From is null ? 0m : await queries.DebtBeforeAsync(customerId, from, cancellationToken);
        var ledger = await queries.StatementLedgerAsync(customerId, from, to, cancellationToken);
        var lines = CustomerStatementLedger.Build(
            ledger.Invoices, ledger.Payments, from, to, ledger.Adjustments, returns: await queries.ReturnsOfAsync(customerId, from, to, cancellationToken), openingDebt: opening);
        var now = await debts.SnapshotAsync(customerId, cancellationToken);
        var entries = lines.Entries.AsEnumerable().Reverse();
        if (query.Kinds is { Count: > 0 } kinds)
        {
            entries = entries.Where(e => kinds.Contains(e.Kind));
        }

        var matching = entries.ToList();
        var (awaiting, pending) = await queries.PaymentCountsAsync(customerId, cancellationToken);
        return new CustomerDebtTab(
            View(customer), now.Debt, now.DebtAgeDays, now.UnpaidInvoices,
            new Paged<CustomerStatementEntry>(matching.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, matching.Count), opening,
            awaiting, pending, now.ReservedAdvances);
    }

    private static CustomerDebtCustomer View(DebtCustomerRow c) => new(c.Id, c.Name, c.PhoneNumber, c.Code);
}
