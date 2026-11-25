using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Queries;
using OMTS.Infrastructure.Persistence.Repositories;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The read screens get the debt age, the unpaid invoices and the statement from SQL; this checks them against <see cref="CustomerDebtAllocator"/> and the whole-history
/// <see cref="CustomerStatementLedger"/> on random ledgers: invoices with returns, cancelled rows, payments that name invoices or parts of them (also more than an invoice needs),
/// payments waiting for their transfer, money paid back, adjustments of both signs, invoices of the same time, and advances reserved for open orders.
/// </summary>
public class CustomerDebtReaderSqlServerTests
{
    private const int Customers = 70;

    [SqlServerFact]
    public async Task The_debt_ages_and_unpaid_invoices_read_by_sql_are_those_of_the_allocator_and_the_statement_those_of_the_whole_history()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        var customerIds = await SeedAsync(host, zone.Id, new Random(24112026));

        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).Options);
        var query = new CustomerDebtQuery(db);
        var reader = new CustomerDebtReader(query, TimeProvider.System);
        var service = new CustomerDebtService(query, reader, new SalesInvoiceCorrectionLog(new SalesInvoiceRepository(db)), TimeProvider.System);
        var today = TimeProvider.System.Today();

        var debtOf = await reader.DebtsAsync(customerIds, CancellationToken.None);
        var ages = await reader.AgesAsync(debtOf, CancellationToken.None);
        var unpaidOf = await reader.UnpaidAmountsAsync(customerIds, CancellationToken.None);
        var owing = 0;
        foreach (var id in customerIds)
        {
            var expected = await reader.AllocationAsync(id, CancellationToken.None);
            Assert.Equal(expected.Debt, debtOf[id]);

            var age = ages[id];
            Assert.Equal(expected.ReservedTotal, age.ReservedTotal);
            if (debtOf[id] > 0m)
            {
                owing++;
                Assert.Equal(expected.OldestUnpaidDate, age.OldestUnpaidDate);
                Assert.Equal(expected.DebtAgeDays, age.DebtAgeDays);
            }

            var mine = InvoicesOf(db, id);
            Assert.Equal(expected.UnpaidInvoices.ToDictionary(i => i.Id, i => i.Unpaid), unpaidOf.Where(u => mine.Contains(u.Key)).ToDictionary(u => u.Key, u => u.Value));

            var snapshot = await reader.SnapshotAsync(id, CancellationToken.None);
            Assert.Equal(expected.Debt, snapshot.Debt);
            Assert.Equal(expected.UnpaidInvoices, snapshot.UnpaidInvoices);
            Assert.Equal(expected.ReservedAdvances, snapshot.ReservedAdvances);
            Assert.Equal(expected.DebtAgeDays, snapshot.DebtAgeDays);
            Assert.Equal(expected.OldestUnpaidDate, snapshot.OldestUnpaidDate);
        }

        // The seed must give the checks something to check.
        Assert.InRange(owing, 15, Customers);
        Assert.Contains(ages.Values, a => a.ReservedTotal > 0m);
        Assert.Contains(customerIds, id => debtOf[id] > 0m && ages[id].ReservedTotal == 0m);

        // The debt list: the same rows and order as the allocator gives, whatever the page.
        var rows = (await service.RowsAsync(null, CancellationToken.None)).Rows;
        var byAllocator = new List<(Guid Id, int? Age, DateOnly? Date)>();
        foreach (var row in rows)
        {
            var allocation = await reader.AllocationAsync(row.Customer.Id, CancellationToken.None);
            Assert.Equal(allocation.Debt > 0 ? allocation.DebtAgeDays : null, row.DebtAgeDays);
            Assert.Equal(allocation.Debt > 0 ? allocation.OldestUnpaidDate : null, row.OldestUnpaidDate);
            Assert.Equal(allocation.ReservedTotal, row.ReservedAdvance);
            byAllocator.Add((row.Customer.Id, row.DebtAgeDays, row.OldestUnpaidDate));
        }

        Assert.Equal(rows.OrderByDescending(r => r.DebtAgeDays ?? -1).ThenBy(r => r.Debt < 0).ThenByDescending(r => Math.Abs(r.Debt)).ThenBy(r => r.Customer.Name, StringComparer.Ordinal).Select(r => r.Customer.Id),
            rows.Select(r => r.Customer.Id));

        // The statement and the debt tab read only the rows of the period; they equal the whole history cut to the period.
        var wholeStart = DateOnly.MinValue;
        var wholeEnd = DateOnly.MaxValue;
        foreach (var id in customerIds.Take(40))
        {
            var ledger = await query.StatementLedgerAsync(id, wholeStart, wholeEnd, CancellationToken.None);
            var returns = await query.ReturnsOfAsync(id, wholeStart, wholeEnd, CancellationToken.None);
            foreach (var (from, to) in new[] { (today.AddDays(-60), today), (today.AddDays(-200), today.AddDays(-90)), (today.AddDays(-400), today.AddDays(-150)), (today.AddDays(5), today.AddDays(9)) })
            {
                var expected = CustomerStatementLedger.Build(ledger.Invoices, ledger.Payments, from, to, ledger.Adjustments, returns: returns);
                var statement = (await service.StatementAsync(id, from, to, CancellationToken.None)).Value!;
                Assert.Equal(expected.OpeningDebt, statement.OpeningDebt);
                Assert.Equal(expected.ClosingDebt, statement.ClosingDebt);
                Assert.Equal(expected.Entries.Select(Key), statement.Entries.Select(Key));
                var tab = (await service.TabAsync(id, new CustomerDebtTabQuery(from, to, PageSize: 100), CancellationToken.None)).Value!;
                Assert.Equal(expected.OpeningDebt, tab.OpeningDebt);
                Assert.Equal(expected.Entries.AsEnumerable().Reverse().Take(100).Select(Key), tab.Entries.Items.Select(Key));
                Assert.Equal(expected.Entries.Count, tab.Entries.Total);
            }

            var whole = CustomerStatementLedger.Build(ledger.Invoices, ledger.Payments, wholeStart, wholeEnd, ledger.Adjustments, returns: returns);
            var wholeTab = (await service.TabAsync(id, new CustomerDebtTabQuery(PageSize: 100), CancellationToken.None)).Value!;
            Assert.Equal(whole.Entries.AsEnumerable().Reverse().Take(100).Select(Key), wholeTab.Entries.Items.Select(Key));
            Assert.Equal(ledger.Payments.Count(p => p.AwaitingConfirmation), wholeTab.AwaitingConfirmationCount);
            Assert.Equal(ledger.Payments.Count(p => p.Pending), wholeTab.PendingTransferCount);
        }
    }

    [SqlServerFact]
    public async Task The_invoice_search_counts_sums_pages_and_filters_paid_in_sql_as_the_allocator_says_and_the_dashboard_figures_are_the_sums_of_the_invoices()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        await SeedAsync(host, zone.Id, new Random(7));

        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).Options);
        var query = new CustomerDebtQuery(db);
        var reader = new CustomerDebtReader(query, TimeProvider.System);
        var search = new SalesInvoiceSearchReader(db);
        var today = TimeProvider.System.Today();
        var filter = new SalesInvoiceSearchFilter(today.AddDays(-250), today, null, null, null, null, null, null, null);

        var all = await db.SalesInvoices.AsNoTracking().Where(i => i.InvoiceDate >= filter.From && i.InvoiceDate <= filter.To).ToListAsync();
        var ownPaid = await db.CustomerPayments.AsNoTracking().Where(p => p.CustomerId == null && p.InvoiceId != null && p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled)
            .GroupBy(p => p.InvoiceId!.Value).Select(g => new { Id = g.Key, Amount = g.Sum(p => p.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Amount);
        Assert.Equal(all.Count, await search.CountAsync(filter, CancellationToken.None));

        var customers = await search.CustomerIdsAsync(filter, CancellationToken.None);
        Assert.Equal(all.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value).Distinct().Order(), customers.Order());
        var unpaid = await reader.UnpaidAmountsAsync(customers, CancellationToken.None);
        var allocations = await reader.AllocationsAsync(customers, CancellationToken.None);

        bool? IsPaid(SalesInvoice i) => i.StatusId == StatusIds.SalesInvoice.Cancelled
            ? null
            : i.CustomerId is { } c ? allocations[c].UnpaidOf(i.Id) == 0m : Math.Max(0m, i.Total - ownPaid.GetValueOrDefault(i.Id)) == 0m;

        var everything = await search.MatchAsync(filter, null, 0, int.MaxValue, CancellationToken.None);
        Assert.Equal(all.Count, everything.Count);
        Assert.Equal(all.Where(i => i.StatusId == StatusIds.SalesInvoice.Completed).Sum(i => i.Total), everything.CompletedTotal);
        Assert.Equal(all.OrderByDescending(i => i.InvoicedAt).ThenByDescending(i => i.Code, StringComparer.OrdinalIgnoreCase).Select(i => i.Id), everything.Keys.Select(k => k.Id));
        var page = await search.MatchAsync(filter, null, 7, 11, CancellationToken.None);
        Assert.Equal(everything.Keys.Skip(7).Take(11).Select(k => k.Id), page.Keys.Select(k => k.Id));
        Assert.Equal(all.Count, page.Count);

        foreach (var paid in new[] { true, false })
        {
            var matched = await search.MatchAsync(filter, new SalesInvoicePaidFilter(paid, unpaid.Keys.ToList()), 0, int.MaxValue, CancellationToken.None);
            var expected = all.Where(i => IsPaid(i) == paid).ToList();
            Assert.Equal(expected.Count, matched.Count);
            Assert.Equal(expected.Select(i => i.Id).Order(), matched.Keys.Select(k => k.Id).Order());
            Assert.Equal(expected.Where(i => i.StatusId == StatusIds.SalesInvoice.Completed).Sum(i => i.Total), matched.CompletedTotal);
            Assert.NotEmpty(expected);
        }

        // The states the page rows get from the unpaid amounts are the allocator's.
        foreach (var key in everything.Keys)
        {
            var state = SalesInvoicePaidState.OfUnpaid(key.StatusId, key.Total, key.CustomerId, key.OwnPayments, unpaid.GetValueOrDefault(key.Id));
            Assert.Equal(IsPaid(all.Single(i => i.Id == key.Id)), state.IsPaid);
        }

        // Dashboard: the sum and the count of one command are those of the invoices.
        var dashboard = new OperationsDashboardQuery(db);
        var from = today.AddDays(-120);
        var revenue = await dashboard.RevenueAsync(from, today, null, CancellationToken.None);
        var counted = all.Where(i => i.InvoiceDate >= from && i.StatusId != StatusIds.SalesInvoice.Cancelled).ToList();
        Assert.Equal((counted.Sum(i => i.Total), counted.Count), (revenue.Total, revenue.InvoiceCount));
        var none = await dashboard.RevenueAsync(today.AddDays(30), today.AddDays(31), null, CancellationToken.None);
        Assert.Equal((0m, 0), (none.Total, none.InvoiceCount));
        var day = await dashboard.DayAsync(today.AddDays(-400), zone.Id, CancellationToken.None);
        Assert.Equal((0, 0m, 0, 0m), (day.InvoiceCount, day.Revenue, day.ReturnCount, day.ReturnValue));
        var returnDay = (await db.SalesReturns.AsNoTracking().Where(r => r.StatusId == StatusIds.SalesReturn.Recorded).FirstAsync()).ReturnDate;
        var returnsOfDay = await db.SalesReturns.AsNoTracking().Where(r => r.StatusId == StatusIds.SalesReturn.Recorded && r.ReturnDate == returnDay).ToListAsync();
        var figures = await dashboard.DayAsync(returnDay, null, CancellationToken.None);
        Assert.Equal((returnsOfDay.Count, returnsOfDay.Sum(r => r.Value)), (figures.ReturnCount, figures.ReturnValue));
    }

    [SqlServerFact]
    public async Task The_customer_list_footer_sums_are_the_sums_of_the_rows_of_every_match_whatever_the_filters()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        await SeedAsync(host, zone.Id, new Random(99));

        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).Options);
        var list = new CustomerListReader(db);
        var today = TimeProvider.System.Today();
        var filters = new[]
        {
            new CustomerSearchFilter(),
            new CustomerSearchFilter(Search: "Reader 01"),
            new CustomerSearchFilter(SalesFrom: today.AddDays(-120), SalesTo: today.AddDays(-30)),
            new CustomerSearchFilter(CurrentDebtFrom: 1m, SalesFrom: today.AddDays(-200)),
            new CustomerSearchFilter(CurrentDebtTo: 0m),
            new CustomerSearchFilter(Search: "no such customer"),
        };
        foreach (var filter in filters)
        {
            var everyone = await list.ListAsync(filter, CancellationToken.None);
            var (items, total, sums) = await list.SearchAsync(filter, 1, 5, CancellationToken.None);
            Assert.Equal(everyone.Count, total);
            Assert.Equal(everyone.Sum(r => r.Debt), sums.Debt);
            Assert.Equal(everyone.Sum(r => r.TotalSales), sums.TotalSales);
            Assert.Equal(everyone.Sum(r => r.NetSales), sums.NetSales);
            Assert.Equal(everyone.Take(5).Select(r => r.Customer.Id), items.Select(r => r.Customer.Id));
        }

        Assert.NotEqual(0m, (await list.SearchAsync(new CustomerSearchFilter(), 1, 5, CancellationToken.None)).Sums.TotalSales);
    }

    [SqlServerFact]
    public async Task The_order_and_disposal_list_footers_are_the_sums_of_their_matches()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        await SeedAsync(host, zone.Id, new Random(5));
        var completed = new ProductDisposal { Code = "XH000001", DisposedAt = DateTimeOffset.UtcNow, ZoneId = zone.Id, StatusId = StatusIds.ProductDisposal.Completed };
        var draft = new ProductDisposal { Code = "XH000002", DisposedAt = DateTimeOffset.UtcNow, ZoneId = zone.Id, StatusId = StatusIds.ProductDisposal.Draft };
        await host.UseDbAsync(db => db.ProductDisposals.AddRange(completed, draft));
        await using (var raw = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).Options))
        {
            // Lines of a product and a lot that need not exist: only the kg are read.
            await raw.Database.ExecuteSqlRawAsync("ALTER TABLE ProductDisposalLines NOCHECK CONSTRAINT ALL");
            raw.ProductDisposalLines.AddRange(
                new ProductDisposalLine { DisposalId = completed.Id, LineNumber = 1, ProductId = Guid.NewGuid(), HarvestLotId = Guid.NewGuid(), Kg = 12.5m },
                new ProductDisposalLine { DisposalId = completed.Id, LineNumber = 2, ProductId = Guid.NewGuid(), HarvestLotId = Guid.NewGuid(), Kg = 7.25m },
                new ProductDisposalLine { DisposalId = draft.Id, LineNumber = 1, ProductId = Guid.NewGuid(), HarvestLotId = Guid.NewGuid(), Kg = 100m });
            await raw.SaveChangesAsync();
        }

        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).Options);
        var disposals = await new ProductDisposalRepository(db).SearchAsync(
            new ProductDisposalFilter(null, null, null, null, null, null, null, null, ProductDisposalSort.Code, false), 1, 10, CancellationToken.None);
        Assert.Equal((2, 19.75m), (disposals.Total, disposals.TotalKg));

        var orders = new SalesOrderRepository(db);
        var filter = new SalesOrderFilter(null, null, null, null, null, null, null, null, null, null, SalesOrderSort.Code, false);
        var (_, total, totals) = await orders.SearchAsync(filter, 1, 5, CancellationToken.None);
        var all = await db.SalesOrders.AsNoTracking().Where(o => o.StatusId != StatusIds.SalesOrder.Cancelled).ToListAsync();
        Assert.Equal(await db.SalesOrders.CountAsync(), total);
        Assert.Equal(all.Sum(o => o.Total), totals.Total);
        var orderIds = all.Select(o => o.Id).ToHashSet();
        Assert.Equal(
            (await db.SalesInvoices.AsNoTracking().Where(i => i.SalesOrderId != null && i.StatusId != StatusIds.SalesInvoice.Cancelled).ToListAsync()).Where(i => orderIds.Contains(i.SalesOrderId!.Value)).Sum(i => i.Total),
            totals.Invoiced);
        Assert.Equal(
            (await db.CustomerPayments.AsNoTracking().Where(p => p.SalesOrderId != null && p.Kind == CustomerPaymentKind.Payment
                && (p.StatusId == StatusIds.CustomerPayment.Completed || p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation)).ToListAsync())
                .Where(p => orderIds.Contains(p.SalesOrderId!.Value)).Sum(p => p.Amount),
            totals.CustomerPaid);
        Assert.NotEqual(0m, totals.Total);
    }

    private static (DateOnly, CustomerStatementEntryKind, Guid, string, decimal, decimal, decimal, bool, bool, string?, string?, string?, string?, string?) Key(CustomerStatementEntry e) =>
        (e.Date, e.Kind, e.Id, e.Code, e.Amount, e.DebtChange, e.DebtAfter, e.Cancelled, e.Pending, e.InvoiceCode, e.RecordedBy?.Name, e.ConfirmedBy?.Name, e.CollectedBy?.Name, e.Note);

    private static List<Guid> InvoicesOf(OmtsDbContext db, Guid customerId) =>
        db.SalesInvoices.AsNoTracking().Where(i => i.CustomerId == customerId).Select(i => i.Id).ToList();

    private static async Task<List<Guid>> SeedAsync(FarmingSqlServerHost host, Guid zoneId, Random random)
    {
        var now = DateTimeOffset.UtcNow;
        var owner = host.Owner.Account.Id;
        var sequence = 0;
        string Next(string prefix) => $"{prefix}{++sequence:D6}";
        var customers = new List<Customer>();
        var invoices = new List<SalesInvoice>();
        var payments = new List<CustomerPayment>();
        var allocations = new List<CustomerPaymentAllocation>();
        var adjustments = new List<CustomerDebtAdjustment>();
        var returns = new List<SalesReturn>();
        var orders = new List<SalesOrder>();
        // A few shared instants make invoices of the same time, which are ordered by code.
        var sharedInstants = new[] { now.AddDays(-30), now.AddDays(-90), now.AddDays(-150) };

        for (var c = 0; c < Customers; c++)
        {
            var customer = new Customer { Code = AcsTestKit.NewCustomerCode(), CooperativeId = host.CooperativeId, Name = $"Reader {c:D3}" };
            customers.Add(customer);
            var own = new List<SalesInvoice>();
            var order = c % 6 == 0
                ? new SalesOrder
                {
                    Code = Next("DH"), CustomerId = customer.Id, Subtotal = 1_500_000m, Total = 1_500_000m, OrderedAt = now, OrderDate = TimeProvider.System.Today(), ReceivedByAccountId = owner, CreatedByAccountId = owner,
                    StatusId = random.Next(3) == 0 ? StatusIds.SalesOrder.Completed : random.Next(2) == 0 ? StatusIds.SalesOrder.Draft : StatusIds.SalesOrder.Delivering,
                }
                : null;
            if (order is not null)
            {
                orders.Add(order);
            }

            for (var i = random.Next(0, 9); i > 0; i--)
            {
                var at = random.Next(3) == 0 ? sharedInstants[random.Next(sharedInstants.Length)] : now.AddDays(-random.Next(0, 400)).AddMinutes(-random.Next(0, 1440));
                var total = random.Next(1, 20) * 100_000m;
                var cancelled = random.Next(10) == 0;
                var invoice = new SalesInvoice
                {
                    Code = Next("HD"), InvoicedAt = at, InvoiceDate = BusinessClock.ToBusinessDate(at), ZoneId = zoneId, CustomerId = customer.Id, Subtotal = total, Total = total,
                    PaymentTerm = SalesPaymentTerm.PayLater, CreatedByAccountId = owner, StatusId = cancelled ? StatusIds.SalesInvoice.Cancelled : StatusIds.SalesInvoice.Completed,
                    CancelledAt = cancelled ? at.AddHours(1) : null, CancelReason = cancelled ? "test" : null,
                    SalesOrderId = order is not null && random.Next(2) == 0 ? order.Id : null,
                };
                own.Add(invoice);
                if (random.Next(7) == 0)
                {
                    var value = random.Next(0, 3) == 0 ? total : random.Next(1, (int)(total / 1000)) * 1000m;
                    returns.Add(new SalesReturn
                    {
                        Code = Next("TH"), InvoiceId = invoice.Id, ReturnDate = invoice.InvoiceDate, Reason = "test", Value = value, RecordedByAccountId = owner,
                        StatusId = random.Next(5) == 0 ? StatusIds.SalesReturn.Cancelled : StatusIds.SalesReturn.Recorded,
                    });
                    if (returns[^1].StatusId == StatusIds.SalesReturn.Cancelled)
                    {
                        (returns[^1].CancelledAt, returns[^1].CancelledByAccountId, returns[^1].CancelReason) = (invoice.InvoicedAt.AddHours(3), owner, "test");
                    }
                }
            }

            invoices.AddRange(own);
            for (var p = random.Next(0, 7); p > 0; p--)
            {
                var paidBack = random.Next(7) == 0;
                var at = now.AddDays(-random.Next(0, 300)).AddMinutes(-random.Next(0, 1440));
                var payment = new CustomerPayment
                {
                    Code = Next("PT"), CustomerId = customer.Id, Kind = paidBack ? CustomerPaymentKind.PaidBack : CustomerPaymentKind.Payment,
                    Amount = random.Next(1, 25) * 50_000m, DiscountAmount = !paidBack && random.Next(5) == 0 ? random.Next(1, 4) * 10_000m : 0m, PaidAt = at, PaymentDate = BusinessClock.ToBusinessDate(at),
                    Method = CustomerPaymentMethod.Cash,
                    StatusId = random.Next(12) switch
                    {
                        0 => StatusIds.CustomerPayment.Cancelled,
                        1 => StatusIds.CustomerPayment.PendingTransfer,
                        2 or 3 => StatusIds.CustomerPayment.AwaitingConfirmation,
                        _ => StatusIds.CustomerPayment.Completed,
                    },
                    CancelledAt = null,
                };
                if (payment.StatusId == StatusIds.CustomerPayment.Cancelled)
                {
                    payment.CancelledAt = at.AddHours(2);
                    payment.CancelReason = "test";
                }

                if (!paidBack && own.Count > 0)
                {
                    var roll = random.Next(10);
                    if (roll < 3)
                    {
                        payment.InvoiceId = own[random.Next(own.Count)].Id;
                    }
                    else if (roll < 6)
                    {
                        foreach (var target in own.OrderBy(_ => random.Next()).Take(random.Next(1, 4)))
                        {
                            // Parts may be more than the invoice needs (a return came after).
                            allocations.Add(new CustomerPaymentAllocation { PaymentId = payment.Id, InvoiceId = target.Id, Amount = random.Next(1, 20) * 50_000m });
                        }
                    }

                    if (order is not null && random.Next(3) == 0)
                    {
                        payment.SalesOrderId = order.Id;
                    }
                }

                payments.Add(payment);
            }

            for (var a = random.Next(0, 3); a > 0; a--)
            {
                var at = now.AddDays(-random.Next(0, 300));
                adjustments.Add(new CustomerDebtAdjustment
                {
                    Code = Next("DC"), CustomerId = customer.Id, Amount = (random.Next(2) == 0 ? 1 : -1) * random.Next(1, 10) * 50_000m, OccurredAt = at,
                    AdjustmentDate = BusinessClock.ToBusinessDate(at), Reason = "test", RecordedByAccountId = owner, CancelledAt = random.Next(7) == 0 ? at.AddHours(1) : null,
                });
            }
        }

        // Walk-in invoices count in no debt: only their own payments cover them.
        for (var w = 0; w < 12; w++)
        {
            var at = now.AddDays(-random.Next(0, 100));
            var total = random.Next(1, 10) * 100_000m;
            var walkIn = new SalesInvoice
            {
                Code = Next("HD"), InvoicedAt = at, InvoiceDate = BusinessClock.ToBusinessDate(at), ZoneId = zoneId, Subtotal = total, Total = total, PaymentTerm = SalesPaymentTerm.PayNow,
                CreatedByAccountId = owner, StatusId = random.Next(6) == 0 ? StatusIds.SalesInvoice.Cancelled : StatusIds.SalesInvoice.Completed,
            };
            if (walkIn.StatusId == StatusIds.SalesInvoice.Cancelled)
            {
                (walkIn.CancelledAt, walkIn.CancelReason) = (at.AddHours(1), "test");
            }

            invoices.Add(walkIn);
            for (var p = random.Next(0, 3); p > 0; p--)
            {
                payments.Add(new CustomerPayment
                {
                    Code = Next("PT"), Kind = CustomerPaymentKind.Payment, Amount = total * random.Next(1, 3) / 2, PaidAt = at, PaymentDate = walkIn.InvoiceDate, Method = CustomerPaymentMethod.Cash,
                    InvoiceId = walkIn.Id, StatusId = random.Next(5) == 0 ? StatusIds.CustomerPayment.PendingTransfer : StatusIds.CustomerPayment.Completed,
                });
            }
        }

        await host.UseDbAsync(db =>
        {
            db.Customers.AddRange(customers);
            db.SalesOrders.AddRange(orders);
            db.SalesInvoices.AddRange(invoices);
        });
        await host.UseDbAsync(db =>
        {
            db.SalesReturns.AddRange(returns);
            db.CustomerPayments.AddRange(payments);
            db.CustomerDebtAdjustments.AddRange(adjustments);
        });
        await host.UseDbAsync(db => db.CustomerPaymentAllocations.AddRange(allocations));
        return customers.Select(c => c.Id).ToList();
    }
}
