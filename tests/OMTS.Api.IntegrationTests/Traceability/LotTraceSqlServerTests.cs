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
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-02 on SQL Server: the trace and the search translate, and a lot on many invoices costs the same number of commands as a lot on two (no query per invoice).</summary>
public class LotTraceSqlServerTests
{
    private const int ManyInvoices = 25;

    [SqlServerFact]
    public async Task A_lot_on_many_invoices_is_traced_with_as_many_commands_as_a_lot_on_two()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var productId = (await Sales.SalesCatalogueTestKit.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var customerId = (await Sales.SalesCatalogueTestKit.CreateCustomerAsync(owner)).Id();
        var manager = (await host.StaffInZoneAsync(zoneId)).Client;
        var few = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var many = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(2, 3))).Id();
        var codes = new List<string>();
        foreach (var (lotId, count) in new[] { (few, 2), (many, ManyInvoices) })
        {
            for (var i = 0; i < count; i++)
            {
                var sold = await manager.SellAsync(Invoice(i % 3 == 0 ? null : customerId, [Line(productId, lotId, 1m)], Cash(ListPrice), lotId));
                codes.Add(sold.GetProperty("invoice").Text("code"));
            }
        }

        var fewCode = (await manager.TraceAsync(few)).Text("lotCode");
        using var counter = new CommandCounter(new SqlConnectionStringBuilder(host.Database.ConnectionString).InitialCatalog);
        var fewCommands = await counter.CountAsync(() => manager.TraceAsync(few));
        var (trace, manyCommands) = await counter.MeasureAsync(() => manager.TraceAsync(many));

        Assert.True(fewCommands > 0, "The counter saw no command.");
        Assert.Equal(fewCommands, manyCommands);
        Assert.Equal(codes.Skip(2), trace.InvoiceCodes());
        Assert.Equal(ManyInvoices, trace.GetProperty("invoicedKg").GetDecimal());
        Assert.Equal(["2: " + seed.Code, "3: " + seed.Code], trace.LaneSeedLots());
        Assert.Equal([many], await manager.FoundAsync(invoiceCode: codes[^1]));
        Assert.Equal([few, many], await manager.FoundAsync(lotCode: fewCode));
        Assert.Equal(HttpStatusCode.OK, (await manager.GetTraceExportAsync(many)).StatusCode);
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
