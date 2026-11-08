using System.Diagnostics;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09 on SQL Server: every filter translates, the wildcards of LIKE are plain text, and two invoices cost the same number of commands as many (no query per invoice).</summary>
public class SalesInvoiceSearchSqlServerTests
{
    private const int ManyInvoices = 15;

    private sealed record Setup(HttpClient Owner, HttpClient Manager, Guid ZoneId, Guid RaftId, Guid LotId, Guid ProductId, Guid CustomerId);

    private static async Task<Setup> SetupAsync(FarmingSqlServerHost host)
    {
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        return new Setup(owner, (await host.StaffInZoneAsync(zoneId)).Client, zoneId, raftId, lotId, productId, customerId);
    }

    private static object CashSale(Setup s, bool walkIn = false) => Invoice(walkIn ? null : s.CustomerId, [Line(s.ProductId, s.LotId, 1m)], Cash(ListPrice), s.LotId);

    [SqlServerFact]
    public async Task The_search_and_the_export_send_the_same_commands_for_two_invoices_as_for_many_and_every_filter_translates()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        for (var i = 0; i < 2; i++)
        {
            await s.Manager.SellAsync(CashSale(s));
        }

        using var counter = new CommandCounter(new SqlConnectionStringBuilder(host.Database.ConnectionString).InitialCatalog);
        // Loads the rights of the caller, so every measure below finds them cached.
        await s.Manager.InvoiceListAsync();
        var fewSearch = await counter.CountAsync(() => s.Manager.InvoiceListAsync());
        var fewPaid = await counter.CountAsync(() => s.Manager.InvoiceListAsync("?paid=true"));
        var fewExport = await counter.CountAsync(() => s.Manager.ExportInvoicesAsync());
        var fewCash = await counter.CountAsync(() => s.Manager.InvoiceListAsync("?cashCollectedByMe=true"));

        for (var i = 0; i < ManyInvoices; i++)
        {
            await s.Manager.SellAsync(CashSale(s, walkIn: i % 3 == 0));
        }

        var (list, manySearch) = await counter.MeasureAsync(() => s.Manager.InvoiceListAsync("?pageSize=100"));
        var manyPaid = await counter.CountAsync(() => s.Manager.InvoiceListAsync("?paid=true&pageSize=100"));
        var manyExport = await counter.CountAsync(() => s.Manager.ExportInvoicesAsync());
        var manyCash = await counter.CountAsync(() => s.Manager.InvoiceListAsync("?cashCollectedByMe=true&pageSize=100"));

        Assert.True(fewSearch > 0, "The counter saw no command.");
        Assert.Equal(fewSearch, manySearch);
        Assert.Equal(fewPaid, manyPaid);
        Assert.Equal(fewExport, manyExport);
        Assert.Equal(fewCash, manyCash);
        Assert.Equal(2 + ManyInvoices, list.Rows().Count);
        Assert.Equal((2 + ManyInvoices) * ListPrice, list.Money("completedTotal"));

        // The other filters translate and find the same invoices (the code column is compared on the database's own collation).
        var code = list.Codes()[0];
        Assert.Equal([code], (await s.Manager.InvoiceListAsync(Query(("code", code.ToLowerInvariant())))).Codes());
        Assert.Equal(2 + ManyInvoices, (await s.Manager.InvoiceListAsync(Query(("harvestLotId", s.LotId), ("raftId", s.RaftId), ("zoneId", s.ZoneId), ("pageSize", 100)))).Rows().Count);
        Assert.Equal(2 + 10, (await s.Manager.InvoiceListAsync(Query(("customerId", s.CustomerId), ("pageSize", 100)))).Rows().Count);
    }

    [SqlServerFact]
    public async Task The_characters_of_like_are_plain_text_and_a_very_long_code_finds_nothing()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var s = await SetupAsync(host);
        await s.Manager.SellAsync(CashSale(s));

        foreach (var text in new[] { "%", "_", "[a-z]", "HD%", "H_", "'", "--", new string('x', 4001) })
        {
            var response = await s.Manager.GetInvoicesAsync(Query(("code", text)));
            var list = await response.ReadAsync(HttpStatusCode.OK);
            Assert.True(list.Rows().Count == 0, $"[{text}] matched");
            Assert.Equal("MSG-GEN-10", list.Text("messageCode"));
        }
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

        public async Task<int> CountAsync<T>(Func<Task<T>> call) => (await MeasureAsync(call)).Commands;

        public async Task<(T Result, int Commands)> MeasureAsync<T>(Func<Task<T>> call)
        {
            var start = Volatile.Read(ref _count);
            var result = await call();
            return (result, Volatile.Read(ref _count) - start);
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
