using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Migration PurchaseReceiptsV2 on a database with receipts: down (lossy) gives each receipt a zone and a raft and each seed
/// line of a Completed receipt its old lot, keeping the seeding events; up numbers the lines, puts the receipt raft on its
/// Seed and Material lines and points the seeding events to the seed lines again.
/// </summary>
public class PurchaseReceiptsMigrationSqlServerTests
{
    [SqlServerFact]
    public async Task PurchaseReceiptsV2_goes_down_and_up_again_keeping_rafts_line_numbers_and_seeding_events()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(
            new LineBody(world.Other, 1, 1_000), world.MaterialForRaft(2, 50_000), new LineBody(world.Seed, 10, 9_000, "LOT-1", world.Raft.Id));
        var seedLine = receipt.LineOf(world.Seed);
        var cycle = new CultivationCycle { RaftId = world.Raft.Id, OpenedAt = DateTimeOffset.UtcNow };
        await host.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.SeedingEvents.Add(new SeedingEvent { CycleId = cycle.Id, SeedLotId = seedLine, SeedingDate = Today, SeedQuantity = 4, RecordedByAccountId = host.Owner.Account.Id });
        });
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000), new LineBody(null, null, null)));

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PurchaseReceiptsV2", StringComparison.Ordinal)) - 1]);

        Assert.Equal(world.Raft.Id, await db.Database.SqlQuery<Guid>($"SELECT RaftId AS [Value] FROM PurchaseReceipts WHERE Id = {receipt.Id()}").SingleAsync());
        Assert.Equal(world.Zone.Id, await db.Database.SqlQuery<Guid>($"SELECT ZoneId AS [Value] FROM PurchaseReceipts WHERE Id = {receipt.Id()}").SingleAsync());
        Assert.Equal($"{receipt.GetProperty("code").GetString()}-S1",
            await db.Database.SqlQuery<string>($"SELECT InternalCode AS [Value] FROM SeedReceiptLots WHERE Id = {seedLine}").SingleAsync());
        Assert.Equal(seedLine, await db.Database.SqlQuery<Guid>($"SELECT SeedLotId AS [Value] FROM SeedingEvents WHERE CycleId = {cycle.Id}").SingleAsync());
        // A draft line without an item cannot exist in the old model.
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM PurchaseReceiptLines WHERE ItemId IS NULL").SingleAsync());

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var lines = await db.PurchaseReceiptLines.AsNoTracking().Include(l => l.Item).Where(l => l.ReceiptId == receipt.Id()).ToListAsync();
        Assert.Equal([1, 2, 3], lines.OrderBy(l => l.Item!.Code, StringComparer.Ordinal).Select(l => l.LineNumber));
        Assert.Equal(world.Raft.Id, lines.Single(l => l.Id == seedLine).RaftId);
        Assert.Equal(world.Raft.Id, lines.Single(l => l.ItemId == world.Material).RaftId);
        Assert.Null(lines.Single(l => l.ItemId == world.Other).RaftId);
        Assert.Equal(seedLine, await db.SeedingEvents.Where(e => e.CycleId == cycle.Id).Select(e => e.SeedLotId).SingleAsync());
    }
}
