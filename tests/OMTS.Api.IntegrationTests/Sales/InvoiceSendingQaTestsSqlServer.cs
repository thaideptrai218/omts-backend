using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of PR 6b on SQL Server: the end of the sale against a Resend at the same moment, and the indexes of Notifications across the migration.</summary>
public class InvoiceSendingQaTestsSqlServer
{
    private const int Rounds = 3;

    // Guard (passes today). BR-SALE-09, GFR-06: the end of the sale and a Resend of the same invoice at once; every answer is a 200 or a 409 MSG-GEN-09, never a 500. The sale
    // is finished once (one audit row, QrDecidedAt set) and each 200 kept exactly one sending.
    [SqlServerFact]
    public async Task A_finish_and_a_resend_at_once_never_fail_and_finish_the_sale_once()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        await using var _ = host;
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var manager = (await host.StaffInZoneAsync(zoneId)).Client;
        var customerId = (await owner.CreateCustomerAsync()).Id();

        for (var round = 0; round < Rounds; round++)
        {
            var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
            var invoice = (await manager.SellAsync(Invoice(customerId, [Line(productId, lot.Id(), 10m)], Cash(300_000m), lot.Id()))).GetProperty("invoice");

            var answers = await Task.WhenAll(manager.FinishAsync(invoice.Id(), false), owner.ResendAsync(invoice.Id()));

            Assert.Equal(HttpStatusCode.OK, answers[0].StatusCode);
            Assert.Contains(answers[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            if (answers[1].StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("MSG-GEN-09", (await answers[1].ReadAsync(HttpStatusCode.Conflict)).Text("code"));
            }

            var oks = answers.Count(a => a.StatusCode == HttpStatusCode.OK);
            var sendings = await host.QueryAsync(db => db.Notifications.AsNoTracking()
                .CountAsync(n => n.RelatedRecordId == invoice.Id() && n.Channel != NotificationChannel.InApp));
            Assert.Equal(oks, sendings);
            Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == invoice.Id() && e.Action == "SalesInvoice.SaleFinished")));
            Assert.NotNull(await host.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.QrDecidedAt).SingleAsync()));
        }
    }

    // Guard (passes today). The ACCOUNT-08 list and unread count read Notifications by recipient: after Up, Down and Up again the two recipient indexes and the foreign key to
    // Accounts are still there (the nullability change rebuilds them on SQL Server).
    [SqlServerFact]
    public async Task The_recipient_indexes_and_foreign_key_of_notifications_survive_the_migration_down_and_up()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var before = await RecipientObjectsAsync(db);

        await migrator.MigrateAsync("20261107112721_SalesInvoices");
        var down = await RecipientObjectsAsync(db);
        await migrator.MigrateAsync();
        var after = await RecipientObjectsAsync(db);

        Assert.Equal(3, before.Count);
        Assert.Equal(before, down);
        Assert.Equal(before, after);
    }

    private static Task<List<string>> RecipientObjectsAsync(DbContext db) =>
        db.Database.SqlQueryRaw<string>(
            """
            SELECT i.name AS [Value] FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('Notifications') AND c.name = 'RecipientAccountId'
            UNION
            SELECT fk.name AS [Value] FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID('Notifications') AND c.name = 'RecipientAccountId'
            """).OrderBy(n => n).ToListAsync();
}
