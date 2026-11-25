using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>
/// The read screens take the debt age and the unpaid invoices from the database (<see cref="ICustomerDebtQuery.UnpaidInvoicesAsync"/>) and fall back to the allocator for a profile that holds a
/// reserved advance; what they assemble from it must be what <see cref="CustomerDebtReader.AllocationAsync"/> (the allocator over the whole ledger) answers.
/// </summary>
public class CustomerDebtReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly TimeProvider Clock = new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(7)));

    private static CustomerLedgerInvoice Invoice(Guid customer, string code, int daysAgo, decimal total, bool cancelled = false) => new(
        customer, Guid.NewGuid(), code, new DateTimeOffset(Today.AddDays(-daysAgo).ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(7)), Today.AddDays(-daysAgo), total, cancelled);

    private static CustomerLedgerPayment Payment(
        Guid customer, decimal amount, int daysAgo = 0, CustomerPaymentKind kind = CustomerPaymentKind.Payment, Guid? orderId = null, bool orderOpen = false) => new(
        customer, Guid.NewGuid(), "PT" + Guid.NewGuid().ToString("N")[..6], kind, Today.AddDays(-daysAgo), amount, CustomerPaymentMethod.Cash, null, null, StatusIds.CustomerPayment.Completed,
        SalesOrderId: orderId, SalesOrderCode: orderId is null ? null : "DH000001", OrderHoldsAdvance: orderOpen);

    /// <summary>A database that answers the unpaid invoices by running the allocator over the same ledger, so the two can only differ in how the reader assembles them.</summary>
    private sealed class FakeQuery(CustomerLedgerRows ledger) : ICustomerDebtQuery
    {
        public List<(IReadOnlyCollection<Guid> Ids, bool OldestOnly)> UnpaidCalls { get; } = [];

        public int LedgerCalls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> FiguresAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerDebtFigures>>(customerIds.ToDictionary(id => id, id => FiguresOf(id)));

        private CustomerDebtFigures FiguresOf(Guid id)
        {
            var allocation = CustomerDebtAllocator.Allocate(ledger.Invoices.Where(i => i.CustomerId == id), ledger.Payments.Where(p => p.CustomerId == id), Today, ledger.Adjustments.Where(a => a.CustomerId == id));
            // Only the debt matters to the reader: carry it in the invoices figure.
            return new CustomerDebtFigures(allocation.Debt, 0m, 0m, 0m);
        }

        public Task<IReadOnlySet<Guid>> CustomersWithReservedAdvanceAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(ledger.Payments.Where(p => p.Reserved && customerIds.Contains(p.CustomerId)).Select(p => p.CustomerId).ToHashSet());

        public Task<IReadOnlyList<CustomerUnpaidInvoiceRow>> UnpaidInvoicesAsync(IReadOnlyCollection<Guid> customerIds, bool oldestOnly, CancellationToken cancellationToken)
        {
            UnpaidCalls.Add((customerIds, oldestOnly));
            Assert.DoesNotContain(ledger.Payments, p => p.Reserved && customerIds.Contains(p.CustomerId));
            var rows = customerIds.SelectMany(id =>
            {
                var allocation = CustomerDebtAllocator.Allocate(ledger.Invoices.Where(i => i.CustomerId == id), ledger.Payments.Where(p => p.CustomerId == id), Today, ledger.Adjustments.Where(a => a.CustomerId == id));
                var unpaid = allocation.UnpaidInvoices.Select(u => new CustomerUnpaidInvoiceRow(id, u.Id, u.Code, u.InvoiceDate, u.Total, u.Unpaid));
                return oldestOnly ? unpaid.Take(1) : unpaid;
            }).ToList();
            return Task.FromResult<IReadOnlyList<CustomerUnpaidInvoiceRow>>(rows);
        }

        public Task<IReadOnlyDictionary<Guid, DateOnly>> DebtOriginDatesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, DateOnly>>(customerIds
                .Select(id => (Id: id, Dates: ledger.Payments.Where(p => p.CustomerId == id && p.Counts && p.Kind == CustomerPaymentKind.PaidBack).Select(p => p.PaymentDate)
                    .Concat(ledger.Adjustments.Where(a => a.CustomerId == id && !a.Cancelled && a.Amount > 0).Select(a => a.AdjustmentDate)).ToList()))
                .Where(x => x.Dates.Count > 0)
                .ToDictionary(x => x.Id, x => x.Dates.Min()));

        public Task<CustomerLedgerRows> LedgerAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
        {
            LedgerCalls++;
            return Task.FromResult(new CustomerLedgerRows(
                ledger.Invoices.Where(i => customerIds.Contains(i.CustomerId)).ToList(), ledger.Payments.Where(p => customerIds.Contains(p.CustomerId)).ToList(),
                ledger.Adjustments.Where(a => customerIds.Contains(a.CustomerId)).ToList()));
        }

        public Task<IReadOnlyDictionary<Guid, CustomerDebtFigures>> AllFiguresAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DebtCustomerRow>> CustomersAsync(IReadOnlyCollection<Guid> customerIds, string? search, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerLedgerRows> StatementLedgerAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerLedgerReturn>> ReturnsOfAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<decimal> DebtBeforeAsync(Guid customerId, DateOnly date, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(int AwaitingConfirmation, int PendingTransfer)> PaymentCountsAsync(Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> AwaitingConfirmationCountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static (FakeQuery Query, CustomerDebtReader Reader) Reader(CustomerLedgerRows ledger)
    {
        var query = new FakeQuery(ledger);
        return (query, new CustomerDebtReader(query, Clock));
    }

    [Fact]
    public async Task The_age_comes_from_the_oldest_unpaid_invoice_the_database_names_and_a_debt_of_money_paid_back_is_as_old_as_that_payment()
    {
        var (plain, owedBack, adjusted) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = new CustomerLedgerRows(
            [Invoice(plain, "HD000001", 30, 1_000_000m), Invoice(plain, "HD000002", 10, 1_000_000m)],
            [Payment(plain, 1_500_000m, 5), Payment(owedBack, 100_000m, 35), Payment(owedBack, 300_000m, 20, CustomerPaymentKind.PaidBack), Payment(adjusted, 50_000m, 45)],
            [new CustomerLedgerAdjustment(adjusted, Guid.NewGuid(), "DC000001", Today.AddDays(-12), DateTimeOffset.UtcNow, 300_000m, "test", false)]);
        var (query, reader) = Reader(rows);

        var debtOf = await reader.DebtsAsync([plain, owedBack, adjusted], CancellationToken.None);
        var ages = await reader.AgesAsync(debtOf, CancellationToken.None);

        // 2,000,000 owed on invoices, 1,500,000 paid: the older invoice is covered, 500,000 of the newer one is not.
        Assert.Equal((500_000m, Today.AddDays(-10), 10), (ages[plain].Debt, ages[plain].OldestUnpaidDate, ages[plain].DebtAgeDays));
        // No invoice at all: 300,000 paid back against 100,000 received leaves 200,000 owed, as old as the money paid back.
        Assert.Equal((200_000m, Today.AddDays(-20), 20), (ages[owedBack].Debt, ages[owedBack].OldestUnpaidDate, ages[owedBack].DebtAgeDays));
        // No invoice either: 300,000 adjusted up against 50,000 received; the adjustment is as old as the debt.
        Assert.Equal((250_000m, Today.AddDays(-12), 12), (ages[adjusted].Debt, ages[adjusted].OldestUnpaidDate, ages[adjusted].DebtAgeDays));
        Assert.Equal(0, query.LedgerCalls);
        Assert.All(query.UnpaidCalls, call => Assert.True(call.OldestOnly));
    }

    [Fact]
    public async Task A_customer_with_no_debt_or_credit_is_not_looked_up_for_an_age()
    {
        var (paid, credit) = (Guid.NewGuid(), Guid.NewGuid());
        var rows = new CustomerLedgerRows(
            [Invoice(paid, "HD000001", 30, 1_000_000m), Invoice(credit, "HD000002", 30, 1_000_000m)], [Payment(paid, 1_000_000m, 20), Payment(credit, 1_400_000m, 20)], []);
        var (query, reader) = Reader(rows);

        var ages = await reader.AgesAsync(await reader.DebtsAsync([paid, credit], CancellationToken.None), CancellationToken.None);

        Assert.Equal((0m, null, null), (ages[paid].Debt, ages[paid].OldestUnpaidDate, ages[paid].DebtAgeDays));
        Assert.Equal((-400_000m, null, null), (ages[credit].Debt, ages[credit].OldestUnpaidDate, ages[credit].DebtAgeDays));
        Assert.Empty(query.UnpaidCalls);
    }

    [Fact]
    public async Task A_customer_holding_a_reserved_advance_is_allocated_from_its_ledger_and_the_database_is_not_asked_for_it()
    {
        var (reserved, plain) = (Guid.NewGuid(), Guid.NewGuid());
        var order = Guid.NewGuid();
        var rows = new CustomerLedgerRows(
            [Invoice(reserved, "HD000001", 30, 1_000_000m), Invoice(plain, "HD000002", 25, 1_000_000m)],
            [Payment(reserved, 400_000m, 3, orderId: order, orderOpen: true)], []);
        var (query, reader) = Reader(rows);

        var ages = await reader.AgesAsync(await reader.DebtsAsync([reserved, plain], CancellationToken.None), CancellationToken.None);
        var expected = await reader.AllocationAsync(reserved, CancellationToken.None);

        Assert.Equal(400_000m, ages[reserved].ReservedTotal);
        Assert.Equal((expected.OldestUnpaidDate, expected.DebtAgeDays), (ages[reserved].OldestUnpaidDate, ages[reserved].DebtAgeDays));
        Assert.Equal((Today.AddDays(-25), 25), (ages[plain].OldestUnpaidDate, ages[plain].DebtAgeDays));
        Assert.Equal([plain], query.UnpaidCalls.Single().Ids);
    }

    [Fact]
    public async Task The_unpaid_amounts_of_the_invoices_are_those_of_the_allocator_whether_or_not_a_customer_holds_a_reserved_advance()
    {
        var (reserved, plain) = (Guid.NewGuid(), Guid.NewGuid());
        var order = Guid.NewGuid();
        var rows = new CustomerLedgerRows(
            [Invoice(reserved, "HD000001", 30, 1_000_000m), Invoice(plain, "HD000002", 25, 1_000_000m), Invoice(plain, "HD000003", 5, 700_000m)],
            [Payment(reserved, 400_000m, 3, orderId: order, orderOpen: true), Payment(plain, 1_200_000m, 2)], []);
        var (_, reader) = Reader(rows);

        var unpaid = await reader.UnpaidAmountsAsync([reserved, plain], CancellationToken.None);

        var expected = (await reader.AllocationsAsync([reserved, plain], CancellationToken.None)).Values.SelectMany(a => a.UnpaidInvoices).ToDictionary(i => i.Id, i => i.Unpaid);
        Assert.Equal(expected, unpaid);
        Assert.Equal(2, unpaid.Count);
    }

    [Fact]
    public async Task The_snapshot_of_a_customer_is_the_allocation_with_the_covered_amounts_and_ages_of_its_unpaid_invoices()
    {
        var customer = Guid.NewGuid();
        var rows = new CustomerLedgerRows(
            [Invoice(customer, "HD000001", 30, 1_000_000m), Invoice(customer, "HD000002", 10, 2_000_000m), Invoice(customer, "HD000003", 1, 500_000m, cancelled: true)],
            [Payment(customer, 1_300_000m, 5)], []);
        var (query, reader) = Reader(rows);

        var snapshot = await reader.SnapshotAsync(customer, CancellationToken.None);
        var expected = await reader.AllocationAsync(customer, CancellationToken.None);

        Assert.Equal(expected.Debt, snapshot.Debt);
        Assert.Equal(expected.UnpaidInvoices, snapshot.UnpaidInvoices);
        Assert.Equal((expected.OldestUnpaidDate, expected.DebtAgeDays), (snapshot.OldestUnpaidDate, snapshot.DebtAgeDays));
        Assert.Equal(expected.ReservedAdvances, snapshot.ReservedAdvances);
        // Only the allocation above reads the ledger.
        Assert.Equal(1, query.LedgerCalls);
    }

    [Fact]
    public async Task The_snapshot_of_a_customer_with_a_reserved_advance_is_the_allocation()
    {
        var customer = Guid.NewGuid();
        var rows = new CustomerLedgerRows([Invoice(customer, "HD000001", 30, 1_000_000m)], [Payment(customer, 400_000m, 3, orderId: Guid.NewGuid(), orderOpen: true)], []);
        var (_, reader) = Reader(rows);

        var snapshot = await reader.SnapshotAsync(customer, CancellationToken.None);

        var expected = await reader.AllocationAsync(customer, CancellationToken.None);
        Assert.Equal(expected.UnpaidInvoices, snapshot.UnpaidInvoices);
        Assert.Equal(expected.ReservedAdvances, snapshot.ReservedAdvances);
        Assert.Equal((expected.Debt, expected.OldestUnpaidDate, expected.DebtAgeDays), (snapshot.Debt, snapshot.OldestUnpaidDate, snapshot.DebtAgeDays));
        Assert.Equal(400_000m, snapshot.ReservedTotal);
    }

    [Fact]
    public void The_paid_state_from_the_unpaid_amount_is_that_of_the_allocation_and_a_walk_in_invoice_is_covered_by_its_own_payments()
    {
        var invoice = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var allocation = new CustomerDebtAllocation(300_000m, [new UnpaidInvoice(invoice, "HD000001", Today, 500_000m, 200_000m, 300_000m, 0)], Today, 0);

        Assert.Equal(
            SalesInvoicePaidState.Of(invoice, StatusIds.SalesInvoice.Completed, 500_000m, customer, 0m, allocation),
            SalesInvoicePaidState.OfUnpaid(StatusIds.SalesInvoice.Completed, 500_000m, customer, 0m, 300_000m));
        Assert.Equal(new SalesInvoicePaidState(true, 500_000m, 0m), SalesInvoicePaidState.OfUnpaid(StatusIds.SalesInvoice.Completed, 500_000m, customer, 0m, 0m));
        Assert.Equal(new SalesInvoicePaidState(false, 200_000m, 300_000m), SalesInvoicePaidState.OfUnpaid(StatusIds.SalesInvoice.Completed, 500_000m, null, 200_000m, 999m));
        Assert.Equal(new SalesInvoicePaidState(null, 0m, 0m), SalesInvoicePaidState.OfUnpaid(StatusIds.SalesInvoice.Cancelled, 500_000m, customer, 0m, 300_000m));
    }
}
