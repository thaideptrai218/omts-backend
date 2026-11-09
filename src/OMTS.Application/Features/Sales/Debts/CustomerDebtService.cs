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
    /// whatever the search. A fixed number of queries, whatever the number of profiles.
    /// </summary>
    public async Task<CustomerDebtRows> RowsAsync(string? search, CancellationToken cancellationToken)
    {
        var debtOf = (await queries.AllFiguresAsync(cancellationToken)).ToDictionary(f => f.Key, f => CustomerDebtMath.Debt(f.Value));
        var totalReceivables = debtOf.Values.Sum(CustomerDebtMath.Receivable);
        var totalOwedBack = debtOf.Values.Sum(CustomerDebtMath.OwedBack);
        var nonZero = debtOf.Where(d => d.Value != 0).Select(d => d.Key).ToList();
        if (nonZero.Count == 0)
        {
            return new CustomerDebtRows([], totalReceivables, totalOwedBack);
        }

        var found = await queries.CustomersAsync(nonZero, search, cancellationToken);
        var allocations = await debts.AllocationsAsync(found.Where(c => debtOf[c.Id] > 0).Select(c => c.Id).ToList(), cancellationToken);
        var rows = found
            .Select(c =>
            {
                var allocation = allocations.GetValueOrDefault(c.Id) ?? CustomerDebtAllocation.None;
                return new CustomerDebtRow(View(c), debtOf[c.Id], allocation.DebtAgeDays, allocation.OldestUnpaidDate);
            })
            // Money owed back has no age: last, largest first.
            .OrderByDescending(r => r.DebtAgeDays ?? -1)
            .ThenBy(r => r.Debt < 0)
            .ThenByDescending(r => Math.Abs(r.Debt))
            .ThenBy(r => r.Customer.Name, StringComparer.Ordinal)
            .ToList();
        return new CustomerDebtRows(rows, totalReceivables, totalOwedBack);
    }

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
        // The staff statement: payments with who recorded, confirmed and collected them, their note and cancel reason (BR-DEBT-04).
        var (invoices, payments) = await queries.StatementLedgerAsync(customerId, cancellationToken);
        // BR-DEBT-04: under each invoice, every correction and price cut, also one made after the period.
        var lines = CustomerStatementLedger.Build(invoices, payments, start, end, await corrections.OfAsync(invoices.Select(i => i.Id).ToList(), cancellationToken));
        var now = CustomerDebtAllocator.Allocate(invoices, payments, today);
        return new CustomerDebtStatement(
            View(customer), start, end, lines.OpeningDebt, lines.Entries, lines.ClosingDebt, now.Debt, now.DebtAgeDays, now.UnpaidInvoices);
    }

    private static CustomerDebtCustomer View(DebtCustomerRow c) => new(c.Id, c.Name, c.PhoneNumber);
}
