using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The SalesInvoices migration (PR 6a) on SQL Server: Up, Down over an invoice that closed a lot, Up again; and the demo sales with their rerun.</summary>
public class SalesInvoicesMigrationAndDemoTests
{
    private const string MigrationBefore = "20261107055301_HarvestLotCorrections";

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<List<string>> NamesAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<string>(sql).ToListAsync();

    [SqlServerFact]
    public async Task Up_down_over_a_sale_that_closed_a_lot_and_up_again_end_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        // A cooperative on the rows before this migration, for the zone of the sale below.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES (NEWID(), N'HTX', SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        var tables = await NamesAsync(db, "SELECT name AS [Value] FROM sys.tables");
        Assert.Contains("SalesInvoices", tables);
        Assert.Contains("SalesInvoiceLines", tables);
        Assert.Contains("SalesInvoicePriceCuts", tables);
        Assert.Contains("CustomerPayments", tables);
        var keys = await NamesAsync(db, "SELECT name AS [Value] FROM sys.foreign_keys");
        Assert.Contains("FK_HarvestLots_SalesInvoices_ClosedBySalesInvoiceId", keys);
        Assert.Contains("FK_SalesInvoiceLines_HarvestLots_HarvestLotId", keys);
        var checks = await NamesAsync(db, "SELECT name AS [Value] FROM sys.check_constraints");
        Assert.Contains("CK_SalesInvoice_Total", checks);
        Assert.Contains("CK_SalesInvoiceLine_Kg", checks);
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_SalesInvoices_DeduplicationKey' AND is_unique = 1 AND has_filter = 1"));
        Assert.Equal(["COMPLETED", "CANCELLED"], await db.Statuses.Where(s => s.Group == StatusGroups.SalesInvoice).OrderBy(s => s.Id).Select(s => s.Code).ToListAsync());
        Assert.Equal(["AWAITING_CONFIRMATION", "COMPLETED", "CANCELLED", "PENDING_TRANSFER"], await db.Statuses.Where(s => s.Group == StatusGroups.CustomerPayment).OrderBy(s => s.Id).Select(s => s.Code).ToListAsync());
        Assert.Equal([200, 210, 211, 212], await db.StatusTransitions.Where(t => t.Id >= 200 && t.Id <= 212).Select(t => t.Id).OrderBy(i => i).ToListAsync());

        // A sale that closed its lot, with its line and its cash payment, on both sides of the Down.
        var coop = await db.Cooperatives.FirstAsync();
        var zone = new FarmingZone { CooperativeId = coop.Id, Code = "KV01", Name = "A" };
        var cycle = new CultivationCycle { Raft = new Raft { Zone = zone, Code = "B01", Size = "1", Lanes = [new Lane { LaneNumber = 1 }] }, StartDate = new DateOnly(2026, 1, 1) };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        var lot = new HarvestLot
        {
            LotCode = "KV01-B01-20260102", Raft = cycle.Raft, Cycle = cycle, HarvestedAt = DateTimeOffset.UtcNow, HarvestDate = new DateOnly(2026, 1, 2), CageCount = 1, KgPerCage = 50,
            EstimatedKg = 50, RecordedByAccountId = account.Id,
        };
        var product = new Product { Name = "Oyster", UnitPrice = 30_000m };
        var invoice = new SalesInvoice
        {
            Code = "HD000001", InvoicedAt = DateTimeOffset.UtcNow, InvoiceDate = new DateOnly(2026, 1, 2), Zone = zone, Subtotal = 6_000_000m, Total = 6_000_000m, CreatedByAccountId = account.Id,
            Lines = [new SalesInvoiceLine { LineNumber = 1, Product = product, HarvestLot = lot, Kg = 200m, UnitPrice = 30_000m, ListPrice = 30_000m, Amount = 6_000_000m, SoldWholeLot = true }],
        };
        invoice.Payments.Add(new CustomerPayment { Code = "PT000001", PaymentDate = invoice.InvoiceDate, Amount = 6_000_000m, Method = CustomerPaymentMethod.Cash, CollectedByAccountId = account.Id, RecordedByAccountId = account.Id });
        db.AddRange(cycle, account, lot, product, invoice);
        await db.SaveChangesAsync();
        (lot.StatusId, lot.CloseDate, lot.LeftoverKg, lot.CloseReason, lot.ClosedBySalesInvoiceId) = (StatusIds.HarvestLot.Closed, invoice.InvoiceDate, 0m, HarvestLotCloseReason.SoldWholeLot, invoice.Id);
        await db.SaveChangesAsync();
        var ids = new { Lot = lot.Id, Invoice = invoice.Id };
        // The check holds the formula of BR-SALE-06.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE SalesInvoices SET Total = 1 WHERE Id = {ids.Invoice}"));
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(MigrationBefore);

        Assert.DoesNotContain("SalesInvoices", await NamesAsync(db, "SELECT name AS [Value] FROM sys.tables"));
        Assert.Equal(1, await ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM HarvestLots WHERE Id = '{ids.Lot}' AND StatusId = 2903 AND ClosedBySalesInvoiceId IS NULL"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id BETWEEN 3501 AND 3603"));

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM SalesInvoices"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Products"));
    }

    [SqlServerFact]
    public async Task The_demo_has_a_cash_sale_and_a_part_cash_pay_later_sale_by_the_zone_managers_and_a_rerun_adds_nothing()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });

            var first = await RunAsync(api);
            Assert.Equal(DeploymentBootstrapper.Success, first.ExitCode);
            Assert.Contains(first.Messages, m => m == "Demo sales invoices: 2 created.");

            var invoices = await UseDbAsync(api, db => db.SalesInvoices.AsNoTracking().OrderBy(i => i.Code)
                .Select(i => new { i.Code, Zone = i.Zone.Code, i.PaymentTerm, i.Total, i.CustomerId, i.PurchaseRequestId, i.TakesRequestInFull, Creator = i.CreatedByAccountId, Payments = i.Payments.Sum(p => p.Amount) })
                .ToListAsync());
            Assert.Equal(["HD000001", "HD000002"], invoices.Select(i => i.Code));
            Assert.Equal((SalesPaymentTerm.PayNow, 7_200_000m, 7_200_000m), (invoices[0].PaymentTerm, invoices[0].Total, invoices[0].Payments));
            Assert.Equal((SalesPaymentTerm.PayLater, 6_000_000m, 1_000_000m, false), (invoices[1].PaymentTerm, invoices[1].Total, invoices[1].Payments, invoices[1].TakesRequestInFull));
            Assert.NotNull(invoices[1].PurchaseRequestId);
            var managers = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").OrderBy(a => a.Username).Select(a => a.Id).ToListAsync());
            Assert.Equal(managers, invoices.Select(i => i.Creator));
            // No demo lot is closed, and the Owner has a notice of each sale.
            Assert.Equal(0, await UseDbAsync(api, db => db.HarvestLots.CountAsync(l => l.StatusId == StatusIds.HarvestLot.Closed)));
            Assert.Equal(2, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "SALE_COMPLETED")));
            Assert.Equal(["PT000001", "PT000002"], await UseDbAsync(api, db => db.CustomerPayments.OrderBy(p => p.Code).Select(p => p.Code).ToListAsync()));
            // TRACE-01 at the end of the cash sale: its lot is the one Published lot and the invoice has its QR code; the pay-later sale is left unfinished.
            var qr = await UseDbAsync(api, db => db.SalesInvoices.OrderBy(i => i.Code).Select(i => new { i.QrToken, i.QrDecidedAt, LotId = i.Lines.Select(l => l.HarvestLotId).First() }).ToListAsync());
            Assert.Equal((true, true, false, false), (qr[0].QrToken is not null, qr[0].QrDecidedAt is not null, qr[1].QrToken is not null, qr[1].QrDecidedAt is not null));
            var published = await UseDbAsync(api, db => db.HarvestLots.Where(l => l.PublishedAt != null).Select(l => l.Id).ToListAsync());
            Assert.Equal([qr[0].LotId], published);
            Assert.Equal(1, await UseDbAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Published" && e.RecordId == qr[0].LotId)));

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Contains(rerun.Messages, m => m == "Demo sales invoices: already there.");
            Assert.Equal(2, await UseDbAsync(api, db => db.SalesInvoices.CountAsync()));
            Assert.Equal(2, await UseDbAsync(api, db => db.Set<CustomerPayment>().CountAsync()));
            Assert.Equal(1, await UseDbAsync(api, db => db.SalesInvoices.CountAsync(i => i.QrToken != null)));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }
}
