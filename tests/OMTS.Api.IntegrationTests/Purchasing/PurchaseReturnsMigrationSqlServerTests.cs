using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Migration PurchaseReturnsV2 on a database with returns: down (lossy) deletes the Cancelled returns and rounds the amounts to
/// thousands, all of them as debt reduction; up makes the rows Recorded, keeps their amounts and codes and drops the refund columns.
/// </summary>
public class PurchaseReturnsMigrationSqlServerTests
{
    [SqlServerFact]
    public async Task PurchaseReturnsV2_goes_down_and_up_again_keeping_the_returns_as_recorded()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 1_500));
        var line = receipt.LineOf(world.Other);
        var mistaken = (await world.ReturnAsync(receipt.Id(), line, 2)).GetProperty("returns")[0].Id();
        await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        var standing = (await world.ReturnAsync(receipt.Id(), line, 1)).GetProperty("returns").EnumerateArray().Single(r => r.Id() != mistaken);
        var (standingId, code) = (standing.Id(), standing.GetProperty("code").GetString());

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PurchaseReturnsV2", StringComparison.Ordinal)) - 1]);

        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM PurchaseReturns WHERE Id = {mistaken}").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM PurchaseReturnLines WHERE ReturnId = {mistaken}").SingleAsync());
        Assert.Equal(2_000m, await db.Database.SqlQuery<decimal>($"SELECT ReturnValue AS [Value] FROM PurchaseReturnLines WHERE ReturnId = {standingId}").SingleAsync());
        Assert.Equal(2_000m, await db.Database.SqlQuery<decimal>($"SELECT DebtReductionAmount AS [Value] FROM PurchaseReturns WHERE Id = {standingId}").SingleAsync());
        Assert.Equal("[]", await db.Database.SqlQuery<string>($"SELECT EvidenceUrls AS [Value] FROM PurchaseReturns WHERE Id = {standingId}").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id = {StatusIds.PurchaseReturn.Recorded}").SingleAsync());

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var kept = await db.PurchaseReturns.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == standingId);
        Assert.Equal((StatusIds.PurchaseReturn.Recorded, code, 2_000m), (kept.StatusId, kept.Code, kept.Lines.Sum(l => l.ReturnValue)));
        Assert.Equal(6_000 - 2_000, await world.DebtAsync());
        // The TH counter survived the way down, so the next return does not take a code that a kept row holds.
        var next = (await world.ReturnAsync(receipt.Id(), line, 1)).GetProperty("returns").EnumerateArray().Single(r => r.Id() != standingId);
        Assert.Equal(Application.Features.Purchasing.DocumentCodes.ReturnCode(3), next.GetProperty("code").GetString());
        await (await world.Owner.CancelReturnAsync(standingId, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(6_000 - 1_500, await world.DebtAsync());
    }
}
