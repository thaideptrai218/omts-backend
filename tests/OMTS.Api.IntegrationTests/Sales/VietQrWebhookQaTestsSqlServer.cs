using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of PR 7 on SQL Server: a transfer racing a change of payment, two different transfers for one sale at the same moment, and the migration
/// 20261108182824_VietQrPendingSales (the filtered unique index on the reference of VietQR payments, down and up). Every answer is a 2xx or a 409, never a 500.
/// </summary>
public class VietQrWebhookQaTestsSqlServer
{
    private const int Rounds = 2;
    private const string MigrationBefore = "20261108020405_CustomerPayments";
    private const string Migration = "20261108182824_VietQrPendingSales";

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.CreateCustomerAsync();
        var manager = await host.StaffInZoneAsync(zoneId);
        await host.UseDbAsync(db => db.Cooperatives.Single(c => c.Id == host.CooperativeId).BankAccount = new ReceivingBankAccount
        {
            BankBin = BankBin, BankName = "Vietcombank", AccountNumber = AccountNumber, AccountHolderName = "HTX DEMO", AcceptedAt = DateTimeOffset.UtcNow,
        });
        return new Setup(host, manager.Client, raftId, product.Id(), customer.Id());
    }

    private static async Task<string> AwaitingAsync(Setup setup, int lane) =>
        (await setup.Manager.AwaitTransferAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, (await setup.Host.Owner.Client.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(lane))).Id(), 10m)], VietQr())))
        .Code();

    private static async Task<HttpResponseMessage> TransferAsync(Setup setup, string code, decimal amount, string reference)
    {
        using var client = setup.Host.Api.CreateClient();
        return await client.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode = code, amount, bankReference = reference });
    }

    private static async Task<string> OutcomeAsync(HttpResponseMessage response) =>
        Assert.Single((await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray()).Outcome();

    // SALE-05.0.E4 step 2 vs SALE-05 step 8: the change of payment and the transfer meet on the row of the sale; one ends it, the other sees how it ended. One invoice,
    // and the transfer is recorded either way (completing the sale, or paying the invoice saved with cash).
    [SqlServerFact]
    public async Task A_transfer_racing_a_change_of_payment_leaves_one_invoice_and_records_the_transfer_either_way()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var code = await AwaitingAsync(setup, round + 1);
            var reference = NewReference();

            var transfer = TransferAsync(setup, code, 300_000m, reference);
            var change = setup.Manager.ChangePaymentAsync(code, Cash(300_000m));
            await Task.WhenAll(transfer, change);

            var outcome = await OutcomeAsync(await transfer);
            var changed = await change;
            Assert.True(changed.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, await changed.Content.ReadAsStringAsync());
            var (status, invoices, methods) = await setup.Host.QueryAsync(async db => (
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync(),
                await db.SalesInvoices.CountAsync(i => i.Code == code),
                await db.CustomerPayments.Where(p => p.Invoice!.Code == code).Select(p => p.Method).OrderBy(m => m).ToListAsync()));
            Assert.Equal(1, invoices);
            if (changed.StatusCode == HttpStatusCode.Created)
            {
                Assert.Equal(("PaidInvoice", PendingSaleStatus.PaymentChanged), (outcome, status));
                Assert.Equal([CustomerPaymentMethod.Cash, CustomerPaymentMethod.VietQrInvoice], methods.OrderBy(m => m.ToString()).ToList());
            }
            else
            {
                Assert.Equal(("CompletedSale", PendingSaleStatus.Completed), (outcome, status));
                Assert.Equal([CustomerPaymentMethod.VietQrInvoice], methods);
            }
        }
    }

    // Two different transfers (two references) for one sale at the same moment: one completes it, the other pays the saved invoice; never two invoices with one code.
    [SqlServerFact]
    public async Task Two_different_transfers_for_one_sale_at_once_save_one_invoice_and_record_both()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var code = await AwaitingAsync(setup, round + 1);

            var answers = await Task.WhenAll(TransferAsync(setup, code, 300_000m, NewReference()), TransferAsync(setup, code, 300_000m, NewReference()));

            var outcomes = new List<string> { await OutcomeAsync(answers[0]), await OutcomeAsync(answers[1]) };
            Assert.Equal(["CompletedSale", "PaidInvoice"], outcomes.Order().ToList());
            var (invoices, transfers, created, status) = await setup.Host.QueryAsync(async db => (
                await db.SalesInvoices.CountAsync(i => i.Code == code),
                await db.CustomerPayments.CountAsync(p => p.Invoice!.Code == code && p.Method == CustomerPaymentMethod.VietQrInvoice),
                await db.AuditEvents.CountAsync(e => e.Action == "SalesInvoice.Created" && e.RecordCode == code),
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync()));
            Assert.Equal((1, 2, 1, PendingSaleStatus.Completed), (invoices, transfers, created, status));
            // Each round: an invoice of 300,000 VND paid twice, so 300,000 VND more owed back.
            Assert.Equal(-300_000m * (round + 1), await setup.Host.Owner.Client.CustomerDebtAsync(setup.CustomerId));
        }
    }

    private static Task<string?> IndexAsync(DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT CONCAT(i.name, ' unique=', i.is_unique, ' filter=', ISNULL(i.filter_definition, '-')) AS [Value] FROM sys.indexes i "
        + "WHERE i.object_id = OBJECT_ID('CustomerPayments') AND i.name = 'IX_CustomerPayments_VietQrBankReference'").SingleOrDefaultAsync();

    private static Task InsertAsync(DbContext db, string code, string method, string? reference) => db.Database.ExecuteSqlRawAsync(
        "INSERT INTO CustomerPayments (Id, Code, CustomerId, Kind, PaymentDate, Amount, Method, StatusId, CreatedAt, BankReference) "
        + "VALUES (NEWID(), {0}, NULL, 'Payment', '2026-11-08', 100000, {1}, 3601, SYSDATETIMEOFFSET(), NULLIF({2}, ''))", code, method, reference ?? string.Empty);

    // Additive: payments saved before (two Other transfers with one reference after MSG-DEBT-05, VietQR rows without a reference) live with the new unique index; the
    // index takes only VietQR rows with a reference, so an Other transfer and a VietQR transfer may share one, two VietQR transfers may not; down and up keep the rows.
    [SqlServerFact]
    public async Task The_vietqr_migration_keeps_the_payments_saved_before_and_its_unique_index_covers_only_vietqr_references()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToArray();
        Assert.Equal(MigrationBefore, all[Array.IndexOf(all, Migration) - 1]);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        await InsertAsync(db, "PT910001", "OtherTransfer", "FT-DEMO-QA-1");
        await InsertAsync(db, "PT910002", "OtherTransfer", "FT-DEMO-QA-1");
        await InsertAsync(db, "PT910003", "VietQrInvoice", null);
        await InsertAsync(db, "PT910004", "VietQrInvoice", null);

        await migrator.MigrateAsync();

        Assert.Equal(4, await db.CustomerPayments.CountAsync());
        Assert.Equal(0, await db.CustomerPayments.CountAsync(p => p.TransferInvoiceCode != null || p.TransferredAt != null));
        var index = await IndexAsync(db);
        Assert.StartsWith("IX_CustomerPayments_VietQrBankReference unique=1 filter=", index);
        Assert.Contains("'VietQrInvoice'", index);
        await InsertAsync(db, "PT910005", "VietQrInvoice", "FT-DEMO-QA-1");
        var twice = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(db, "PT910006", "VietQrInvoice", "FT-DEMO-QA-1"));
        Assert.Contains(twice.Number, new[] { 2601, 2627 });
        // SQL Server's default collation ignores case: "ft-demo-qa-1" is the same reference for the index too.
        await Assert.ThrowsAsync<SqlException>(() => InsertAsync(db, "PT910007", "VietQrInvoice", "ft-demo-qa-1"));

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(5, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM CustomerPayments").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'PendingSales'").SingleAsync());

        await migrator.MigrateAsync();
        Assert.Equal(5, await db.CustomerPayments.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
