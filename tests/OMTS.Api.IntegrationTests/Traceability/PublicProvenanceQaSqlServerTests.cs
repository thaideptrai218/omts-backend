using System.Diagnostics;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of ORIGIN-01 on SQL Server (OMTS-222): the page of an invoice with many lots, photos, seedings and a Withdrawn lot costs the same number of commands as the page of an invoice
/// with two (no query per lot or per photo, NFR-10), and a lane re-hung with another seed item shows both items (BR-TRACE-03, BR-ORIGIN-03).
/// </summary>
public class PublicProvenanceQaSqlServerTests
{
    private const int ManyLots = 8;

    [SqlServerFact]
    public async Task Qa_an_invoice_with_many_lots_costs_as_many_commands_as_an_invoice_with_two()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(lanes: ManyLots))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync(quantity: 500m);
        var otherSeed = await owner.SeedLotAsync(quantity: 500m);
        await owner.SeedingRecordAsync(cycleId, seed.Session(Enumerable.Range(1, ManyLots).ToList(), date: Today.AddDays(-5)));
        // Lane 1 re-hung two days later with seed of another item (BR-CYCLE-02 re-hanging).
        await owner.SeedingRecordAsync(cycleId, otherSeed.Session([1], date: Today.AddDays(-3)));
        var lotIds = new List<Guid>();
        for (var lane = 1; lane <= ManyLots; lane++)
        {
            lotIds.Add((await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(lane))).Id());
        }

        await host.UseDbAsync(db =>
        {
            foreach (var lotId in lotIds)
            {
                db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = lotId, SortOrder = 1, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
                db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = lotId, SortOrder = 2, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
            }

            db.RaftPhotos.Add(new RaftPhoto { RaftId = raftId, SortOrder = 1, FileKey = $"{FileCategories.RaftPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
        });
        var productId = (await Catalogue.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var customerId = (await Catalogue.CreateCustomerAsync(owner)).Id();
        var fewToken = await SoldWithQrCodeAsync(owner, productId, customerId, lotIds.Take(2).ToList());
        var manyToken = await SoldWithQrCodeAsync(owner, productId, customerId, lotIds);
        var withdrawnId = lotIds[^1];
        await host.UseDbAsync(db => db.HarvestLots.Where(l => l.Id == withdrawnId).ExecuteUpdate(s => s.SetProperty(l => l.WithdrawnAt, DateTimeOffset.UtcNow)));
        var anonymous = host.Api.CreateClient();
        await anonymous.ProvenanceAsync(fewToken);
        await anonymous.ProvenanceAsync(manyToken);

        using var counter = new CommandCounter(new SqlConnectionStringBuilder(host.Database.ConnectionString).InitialCatalog);
        var fewCommands = await counter.CountAsync(() => anonymous.ProvenanceAsync(fewToken));
        var (page, manyCommands) = await counter.MeasureAsync(() => anonymous.ProvenanceAsync(manyToken));

        Assert.True(fewCommands > 0, "The counter saw no command.");
        Assert.Equal(fewCommands, manyCommands);
        // Invoice with lines, cooperative, certifications, lots (split: lots, lot photos, raft photos), seedings.
        Assert.Equal(7, manyCommands);
        var lots = page.GetProperty("lots").EnumerateArray().ToList();
        Assert.Equal(ManyLots, lots.Count);
        Assert.Equal(["Published", "Withdrawn"], lots.Select(l => l.Text("originState")).Distinct());
        var first = lots[0].GetProperty("origin");
        var items = await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == seed.ItemId || i.Id == otherSeed.ItemId).ToDictionaryAsync(i => i.Id, i => i.Name));
        Assert.Equal([(Today.AddDays(-5), items[seed.ItemId]), (Today.AddDays(-3), items[otherSeed.ItemId])],
            first.GetProperty("seedings").EnumerateArray().Select(s => (s.Date("seedingDate"), s.Text("seedItemName"))));
        Assert.Equal((5, 2, 1), (first.GetProperty("daysOfFarming").GetInt32(), first.GetProperty("harvestPhotoUrls").GetArrayLength(), first.GetProperty("raftPhotoUrls").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetProvenanceAsync(manyToken.ToLowerInvariant() == manyToken ? manyToken.ToUpperInvariant() : manyToken.ToLowerInvariant())).StatusCode);
    }

    private static async Task<string> SoldWithQrCodeAsync(HttpClient owner, Guid productId, Guid customerId, IReadOnlyList<Guid> lotIds)
    {
        var invoice = (await owner.SellAsync(Invoice(customerId, lotIds.Select(id => Line(productId, id, 1m)).ToArray(), Cash(lotIds.Count * ListPrice), lotIds.ToArray())))
            .GetProperty("invoice");
        return (await owner.FinishedAsync(invoice.Id(), wantsQrCode: true)).Text("qrUrl").Split('/')[^1];
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
