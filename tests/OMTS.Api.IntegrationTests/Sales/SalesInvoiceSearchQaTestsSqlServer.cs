using System.Diagnostics;
using System.Net;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-09 on SQL Server: the commands for few and many rows of each kind, a list above the parameter limit, plain-text wildcards and the extreme dates.</summary>
public class SalesInvoiceSearchQaTestsSqlServer(ITestOutputHelper output)
{
    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, Actor Manager, Guid ZoneId, Guid RaftId, Guid LotId, Guid ProductId);

    private static async Task<Setup> SetupAsync(FarmingSqlServerHost host)
    {
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await owner.CreateProductAsync(unitPrice: SalesInvoiceTestKit.ListPrice)).Id();
        return new Setup(host, owner, await host.StaffInZoneAsync(zoneId), zoneId, raftId, lotId, productId);
    }

    private static SalesInvoice NewInvoice(Setup s, Guid? customerId = null, DateTimeOffset? at = null, string? code = null, decimal total = 100_000m) => new()
    {
        Code = code ?? "HD" + AcsTestKit.NewCode(), InvoicedAt = at ?? DateTimeOffset.UtcNow, InvoiceDate = Today, ZoneId = s.ZoneId, CustomerId = customerId, Subtotal = total, Total = total,
        PaymentTerm = SalesPaymentTerm.PayNow, CreatedByAccountId = s.Host.Owner.Account.Id, StatusId = StatusIds.SalesInvoice.Completed,
    };

    private static CustomerPayment Payment(Guid? customerId, Guid invoiceId, Guid collector, decimal amount = 1_000m) => new()
    {
        Code = "PT" + AcsTestKit.NewCode(), CustomerId = customerId, InvoiceId = invoiceId, Kind = CustomerPaymentKind.Payment, Amount = amount, Method = CustomerPaymentMethod.Cash, PaymentDate = Today,
        StatusId = StatusIds.CustomerPayment.AwaitingConfirmation, CollectedByAccountId = collector, RecordedByAccountId = collector,
    };

    private static Notification Sending(Guid invoiceId) => new()
    {
        Channel = NotificationChannel.Sms, Type = SalesInvoiceMessages.MessageType, Title = SalesInvoiceMessages.Title, RelatedRecordType = RelatedRecordTypes.SalesInvoice, RelatedRecordId = invoiceId,
        CreatedAt = DateTimeOffset.UtcNow, PhoneNumber = "999900000001", DeliveryStatusId = StatusIds.MessageDelivery.Sent, DeliveryStatusAt = DateTimeOffset.UtcNow,
    };

    // N+1: many customers (the developer's test had one) with a payment and a sending each.
    [SqlServerFact]
    public async Task The_search_the_paid_filter_and_the_file_send_the_same_commands_for_two_customers_as_for_forty()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        await SeedInvoicesAsync(s, 2);
        using var counter = new CommandCounter(new SqlConnectionStringBuilder(host.Database.ConnectionString).InitialCatalog);
        await s.Manager.Client.InvoiceListAsync();
        var few = await MeasureAllAsync(s, counter);

        await SeedInvoicesAsync(s, 38);

        var many = await MeasureAllAsync(s, counter);
        Assert.True(few.All(c => c > 0), "The counter saw no command.");
        Assert.Equal(few, many);
    }

    private static async Task<int[]> MeasureAllAsync(Setup s, CommandCounter counter) =>
    [
        await counter.CountAsync(() => s.Manager.Client.InvoiceListAsync("?pageSize=100")),
        await counter.CountAsync(() => s.Manager.Client.InvoiceListAsync("?paid=false&pageSize=100")),
        await counter.CountAsync(() => s.Manager.Client.InvoiceListAsync("?cashCollectedByMe=true&pageSize=100")),
        await counter.CountAsync(() => s.Manager.Client.ExportInvoicesAsync()),
    ];

    private static async Task SeedInvoicesAsync(Setup s, int count)
    {
        var customers = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            customers.Add((await s.Owner.CreateCustomerAsync()).Id());
        }

        var invoices = customers.Select(c => NewInvoice(s, c)).ToList();
        await s.Host.UseDbAsync(db =>
        {
            db.SalesInvoices.AddRange(invoices);
            db.CustomerPayments.AddRange(invoices.Select(i => Payment(i.CustomerId, i.Id, s.Manager.Account.Id)));
            db.Notifications.AddRange(invoices.Select(i => Sending(i.Id)));
        });
    }

    // N+1 on the detail and the print: lines, payments and sendings.
    [SqlServerFact]
    public async Task The_detail_and_the_print_send_the_same_commands_for_few_and_many_lines_payments_and_sendings()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        var customer = (await s.Owner.CreateCustomerAsync()).Id();
        using var counter = new CommandCounter(new SqlConnectionStringBuilder(host.Database.ConnectionString).InitialCatalog);
        var small = await InvoiceOfAsync(s, customer, lines: 1, payments: 1, sendings: 1);
        var large = await InvoiceOfAsync(s, customer, lines: 30, payments: 30, sendings: 30);
        await s.Manager.Client.InvoiceAsync(small.Id);

        var few = await counter.CountAsync(() => s.Manager.Client.InvoiceAsync(small.Id));
        var many = await counter.CountAsync(() => s.Manager.Client.InvoiceAsync(large.Id));
        var fewPrint = await counter.CountAsync(() => s.Manager.Client.PrintInvoiceAsync(small.Id));
        var manyPrint = await counter.CountAsync(() => s.Manager.Client.PrintInvoiceAsync(large.Id));

        Assert.True(few > 0, "The counter saw no command.");
        Assert.Equal((few, fewPrint), (many, manyPrint));
    }

    private static async Task<SalesInvoice> InvoiceOfAsync(Setup s, Guid customer, int lines, int payments, int sendings)
    {
        var invoice = NewInvoice(s, customer);
        await s.Host.UseDbAsync(db =>
        {
            db.SalesInvoices.Add(invoice);
            db.Set<SalesInvoiceLine>().AddRange(Enumerable.Range(1, lines).Select(n => new SalesInvoiceLine
            {
                InvoiceId = invoice.Id, LineNumber = n, ProductId = s.ProductId, HarvestLotId = s.LotId, Kg = 1m, UnitPrice = 1_000m, ListPrice = 1_000m, Amount = 1_000m,
            }));
            db.CustomerPayments.AddRange(Enumerable.Range(0, payments).Select(_ => Payment(customer, invoice.Id, s.Manager.Account.Id)));
            db.Notifications.AddRange(Enumerable.Range(0, sendings).Select(_ => Sending(invoice.Id)));
        });
        return invoice;
    }

    // A list above the 2100 parameters of SQL Server, read as one file: how long it takes and what it allocates, for the report.
    [SqlServerFact]
    public async Task A_file_of_three_thousand_invoices_above_the_parameter_limit_is_complete()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        const int Count = 3_000;
        var invoices = Enumerable.Range(0, Count).Select(_ => NewInvoice(s)).ToList();
        await host.UseDbAsync(db => db.SalesInvoices.AddRange(invoices));

        var allocated = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        var response = await s.Manager.Client.ExportInvoicesAsync();
        watch.Stop();

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        output.WriteLine($"{Count} invoices: {watch.ElapsedMilliseconds} ms, {(GC.GetTotalAllocatedBytes() - allocated) / 1_048_576} MB allocated, {response.Content.Headers.ContentLength / 1024} KB file");
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        Assert.Equal(Count + 2, workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber());

        // One page of the list: the matches are read as keys and paged in the application.
        var pageWatch = Stopwatch.StartNew();
        var list = await s.Manager.Client.InvoiceListAsync("?pageSize=20&page=2");
        output.WriteLine($"One page of {Count} matches: {pageWatch.ElapsedMilliseconds} ms");
        Assert.Equal((20, Count), (list.Rows().Count, list.GetProperty("page").GetProperty("total").GetInt32()));
    }

    // PM answer 12/11/2026 (OMTS-224 [5.7]): the file holds at most 5,000 invoices; one more is 400 MSG-GEN-01 on Filters, on the real TOP and ORDER BY, and the screen still lists them.
    [SqlServerFact]
    public async Task A_selection_of_five_thousand_and_one_invoices_is_not_exported_and_five_thousand_are()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        var invoices = Enumerable.Range(0, SalesInvoiceSearchService.MaxExportRows).Select(_ => NewInvoice(s)).ToList();
        await host.UseDbAsync(db => db.SalesInvoices.AddRange(invoices));

        var atTheLimit = await s.Manager.Client.ExportInvoicesAsync();
        Assert.True(atTheLimit.StatusCode == HttpStatusCode.OK, await atTheLimit.Content.ReadAsStringAsync());
        using (var workbook = new XLWorkbook(await atTheLimit.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(SalesInvoiceSearchService.MaxExportRows + 2, workbook.Worksheet(SalesInvoiceExportSheets.ListSheet).LastRowUsed()!.RowNumber());
        }

        var one = NewInvoice(s);
        await host.UseDbAsync(db => db.SalesInvoices.Add(one));

        var refused = await s.Manager.Client.ExportInvoicesAsync();
        var problem = await refused.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Filters"), (problem.Text("code"), problem.Text("field")));
        var list = await s.Manager.Client.InvoiceListAsync("?pageSize=20");
        Assert.Equal(SalesInvoiceSearchService.MaxExportRows + 1, list.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.Client.ExportInvoicesAsync(Query(("code", one.Code)))).StatusCode);
    }

    // Every filter at once, the characters of LIKE as text that a code really holds, and the extreme dates, on the real translation.
    [SqlServerFact]
    public async Task Every_filter_at_once_translates_and_wildcards_match_only_codes_that_hold_them()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        var customer = (await s.Owner.CreateCustomerAsync()).Id();
        var special = NewInvoice(s, customer, code: "HD%_[]\\" + AcsTestKit.NewCode());
        var plain = NewInvoice(s, customer);
        await host.UseDbAsync(db => db.SalesInvoices.AddRange(special, plain));

        foreach (var text in new[] { "%", "_", "[", "]", "\\", "%_[]\\" })
        {
            Assert.Equal([special.Code], (await s.Owner.InvoiceListAsync(Query(("code", text)))).Codes());
        }

        var all = await s.Owner.GetInvoicesAsync(Query(
            ("from", Today), ("to", Today), ("zoneId", s.ZoneId), ("customerId", customer), ("harvestLotId", s.LotId), ("raftId", s.RaftId), ("statusId", StatusIds.SalesInvoice.Completed),
            ("paid", false), ("code", "HD"), ("orderCode", "DH"), ("cashCollectedByMe", true), ("pageSize", 100)));
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);

        foreach (var query in new[] { "?from=0001-01-01&to=0001-01-02", "?from=9999-12-31&to=9999-12-31", "?from=9995-01-01&to=9999-12-31" })
        {
            Assert.Equal(HttpStatusCode.OK, (await s.Owner.GetInvoicesAsync(query)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await s.Owner.ExportInvoicesAsync(query)).StatusCode);
        }
    }

    // Equal times on the real ORDER BY.
    [SqlServerFact]
    public async Task Invoices_of_the_same_moment_keep_one_order_across_the_pages()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        var at = DateTimeOffset.UtcNow;
        var invoices = Enumerable.Range(0, 7).Select(_ => NewInvoice(s, at: at)).ToList();
        await host.UseDbAsync(db => db.SalesInvoices.AddRange(invoices));

        var codes = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            codes.AddRange((await s.Owner.InvoiceListAsync($"?pageSize=3&page={page}")).Codes());
        }

        Assert.Equal(7, codes.Distinct().Count());
        Assert.Equal(invoices.Select(i => i.Code).OrderDescending(StringComparer.Ordinal), codes);
    }

    /// <summary>Counts the EF commands sent to one database (the test's own), whatever runs in parallel against other databases.</summary>
    private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public CommandCounter(string database)
        {
            _database = database;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public async Task<int> CountAsync<T>(Func<Task<T>> call)
        {
            var start = Volatile.Read(ref _count);
            await call();
            return Volatile.Read(ref _count) - start;
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData data && data.Command.Connection?.Database == _database)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                _subscriptions.ForEach(s => s.Dispose());
            }
        }
    }
}
