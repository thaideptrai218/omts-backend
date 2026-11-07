using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H2 on SQL Server: a sale racing the daily close check, and the migration 20261107164749_HarvestLotAutoClose down and up.</summary>
public class HarvestLotCloseQaTestsSqlServer
{
    private const int Rounds = 4;
    private const string MigrationBefore = "20261107145951_SalesInvoiceSending";
    private const string Migration = "20261107164749_HarvestLotAutoClose";

    // A sale on the close day, at the same moment as the check: whoever saves first wins on the row version, the other never answers 500. A partial sale that wins leaves the lot for sale, so
    // the check (retrying the lot) still closes it with what remains; a whole-lot sale that wins closes it itself; a sale that loses is told MSG-GEN-09. The lot is closed once in every case.
    [SqlServerFact]
    public async Task A_sale_racing_the_check_on_the_close_day_closes_the_lot_once_and_never_answers_500()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var productId = (await OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var customerId = (await OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit.CreateCustomerAsync(owner)).Id();
        var manager = await host.StaffInZoneAsync(zoneId);

        for (var round = 0; round < Rounds; round++)
        {
            var whole = round % 2 == 1;
            var lotId = (await manager.Client.HarvestLotAsync(raftId, HarvestFields.OfLanes(round % 5 + 1))).Id();
            var body = whole
                ? Invoice(customerId, [Line(productId, lotId, 20m)], Cash(600_000m))
                : Invoice(customerId, [Line(productId, lotId, 20m)], Cash(600_000m), lotId);

            var sale = manager.Client.PostInvoiceAsync(body);
            var check = Task.Run(() => host.Api.AutoCloseOnAsync(Today.AddDays(4)));
            await Task.WhenAll(sale, check);

            var answer = await sale;
            Assert.True(answer.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"round {round}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
            if (answer.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("MSG-GEN-09", (await answer.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
            }

            var lot = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));
            var sold = answer.StatusCode == HttpStatusCode.Created;
            var expected = (sold, whole) switch
            {
                (true, true) => (HarvestLotCloseReason.SoldWholeLot, 0m),
                (true, false) => (HarvestLotCloseReason.TimeLimitReached, 380m),
                _ => (HarvestLotCloseReason.TimeLimitReached, 400m),
            };
            Assert.Equal((StatusIds.HarvestLot.Closed, expected), (lot.StatusId, (lot.CloseReason!.Value, lot.LeftoverKg!.Value)));
            Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed)));
            Assert.Equal(sold ? 1 : 0, await host.QueryAsync(db => db.SalesInvoiceLines.CountAsync(l => l.HarvestLotId == lotId)));
        }

        Assert.DoesNotContain(host.Api.Logs.Lines, l => l.Contains("Daily close check failed", StringComparison.Ordinal));
    }

    private static Task<List<string>> ColumnAsync(DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT CONCAT(ty.name, ' null=', c.is_nullable) AS [Value] FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
        + "WHERE c.object_id = OBJECT_ID('HarvestLots') AND c.name = 'AutoCloseWarnedFor'").ToListAsync();

    // The migration sits right after SalesInvoiceSending, adds one nullable date column, goes down and up again, and leaves no model change pending.
    [SqlServerFact]
    public async Task The_auto_close_migration_follows_sales_invoice_sending_and_goes_down_and_up()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToArray();
        Assert.Equal([MigrationBefore, Migration], all[^2..]);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync();
        Assert.Equal(["date null=1"], await ColumnAsync(db));

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Empty(await ColumnAsync(db));
        Assert.Equal(MigrationBefore, (await db.Database.GetAppliedMigrationsAsync()).Last());

        await migrator.MigrateAsync();
        Assert.Equal(["date null=1"], await ColumnAsync(db));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
