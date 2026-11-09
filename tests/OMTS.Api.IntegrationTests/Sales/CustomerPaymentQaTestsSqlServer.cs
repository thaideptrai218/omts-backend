using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using SqlKit = OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of PR 8b on SQL Server: two payments of money back that each fit the money owed back but not together (DEBT-02.3.E1 must hold under a race), and the migration
/// 20261108020405_CustomerPayments over existing payments, down and up.
/// </summary>
public class CustomerPaymentQaTestsSqlServer
{
    private const string MigrationBefore = "20261107164749_HarvestLotAutoClose";
    private const string Migration = "20261108020405_CustomerPayments";

    /// <summary>Holds each request after its last check (the bank reference of a transfer) until the other one has passed it too.</summary>
    private sealed class Barrier(int parties)
    {
        private readonly TaskCompletionSource _everyoneRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task PassAsync()
        {
            var arrival = Interlocked.Increment(ref _arrived);
            if (arrival >= parties)
            {
                _everyoneRead.TrySetResult();
            }

            if (arrival <= parties)
            {
                await _everyoneRead.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    }

    private sealed class HeldReferenceCheck(ICustomerPaymentRepository inner, Barrier barrier) : ICustomerPaymentRepository
    {
        public void Add(CustomerPayment payment) => inner.Add(payment);

        public Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) => inner.GetForEditAsync(paymentId, cancellationToken);

        public Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken) =>
            inner.GetForEditAsync(paymentIds, cancellationToken);

        public Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) => inner.GetAsync(paymentId, cancellationToken);

        public Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) => inner.FindByDeduplicationKeyAsync(key, cancellationToken);

        public async Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken)
        {
            var found = await inner.FindNotCancelledByBankReferenceAsync(bankReference, cancellationToken);
            await barrier.PassAsync();
            return found;
        }

        public Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            inner.AwaitingConfirmationAsync(page, pageSize, cancellationToken);

        public Task<CustomerPayment?> FindRecordedTransferAsync(string bankReference, CancellationToken cancellationToken) =>
            inner.FindRecordedTransferAsync(bankReference, cancellationToken);

        public Task<IReadOnlyDictionary<string, decimal>> ReceivedWithoutInvoiceAsync(IReadOnlyCollection<string> invoiceCodes, CancellationToken cancellationToken) =>
            inner.ReceivedWithoutInvoiceAsync(invoiceCodes, cancellationToken);
    }

    // DEBT-02.3.E1 / BR-DEBT-03 "Money paid back ... only up to the money owed back": 200,000 VND owed back, two Accountants pay back 150,000 VND each at the same moment.
    // Both read the same debt; only one may be saved, the other is refused (MSG06, read again), so the cooperative never pays back more than it owed.
    [SqlServerFact]
    public async Task Two_payments_of_money_back_that_together_pass_the_money_owed_back_never_both_save()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        var manager = (await host.StaffInZoneAsync(zoneId)).Client;
        var accountant = await SqlKit.StaffAsync(host, null, StaffRole.Accountant);
        (await manager.SellAsync(Invoice(customerId, [Line(productId, lotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lotId))).GetProperty("invoice");
        await accountant.PaidAsync(MoneyBody(customerId, 500_000m).With("confirmAboveDebt", true));
        Assert.Equal(-200_000m, await owner.CustomerDebtAsync(customerId));

        var barrier = new Barrier(2);
        await using var api = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ICustomerPaymentRepository));
            services.Remove(real);
            services.AddScoped<ICustomerPaymentRepository>(provider =>
                new HeldReferenceCheck((ICustomerPaymentRepository)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), barrier));
        }));
        var held = api.CreateClient();
        held.DefaultRequestHeaders.Authorization = accountant.DefaultRequestHeaders.Authorization;

        var answers = await SqlKit.InParallelAsync(2, _ => held.PayBackAsync(MoneyBody(customerId, 150_000m)));

        var codes = new[] { await SqlKit.CodeOrStatusAsync(answers[0]), await SqlKit.CodeOrStatusAsync(answers[1]) };
        Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.Created));
        Assert.Contains(codes, c => c is "409 MSG06" or "400 MSG-DEBT-06");
        Assert.Equal(-50_000m, await owner.CustomerDebtAsync(customerId));
        Assert.Equal(1, await host.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.CustomerId == customerId && p.Kind == CustomerPaymentKind.PaidBack)));
        Assert.Equal(2, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == host.Owner.Account.Id)));
    }

    // The columns that exist before the migration.
    private static Task<List<string>> PaymentsAsync(DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT CONCAT(Code, ' ', Kind, ' ', Method, ' ', Amount, ' ', StatusId, ' ', CONVERT(varchar(10), PaymentDate, 23), ' ', CONVERT(varchar(20), RowVersion, 1)) AS [Value] "
        + "FROM CustomerPayments ORDER BY Code").ToListAsync();

    private static Task<int> NewColumnsSetAsync(DbContext db) => db.Database.SqlQueryRaw<int>(
        "SELECT COUNT(*) AS [Value] FROM CustomerPayments WHERE BankReference IS NOT NULL OR Note IS NOT NULL OR DeduplicationKey IS NOT NULL OR ConfirmedAt IS NOT NULL "
        + "OR ConfirmedByAccountId IS NOT NULL OR CancelledAt IS NOT NULL OR CancelledByAccountId IS NOT NULL OR CancelReason IS NOT NULL").SingleAsync();

    private static Task<List<string>> IndexesAsync(DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT CONCAT(i.name, ' unique=', i.is_unique, ' filter=', ISNULL(i.filter_definition, '-')) AS [Value] FROM sys.indexes i "
        + "WHERE i.object_id = OBJECT_ID('CustomerPayments') AND i.name IN ('IX_CustomerPayments_DeduplicationKey', 'IX_CustomerPayments_StatusId_PaymentDate', 'IX_CustomerPayments_StatusId') "
        + "ORDER BY i.name").ToListAsync();

    // Additive only: the payments saved before (port cash Awaiting confirmation, one Confirmed, one Cancelled) keep every value, get NULL in the new columns, and two of
    // them without a key live with the unique key index; down and up again keep them; no model change is left.
    [SqlServerFact]
    public async Task The_customer_payments_migration_keeps_the_payments_already_saved_and_goes_down_and_up()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToArray();
        Assert.Equal(MigrationBefore, all[Array.IndexOf(all, Migration) - 1]);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO CustomerPayments (Id, Code, CustomerId, Kind, PaymentDate, Amount, Method, StatusId, CreatedAt) VALUES "
            + "(NEWID(), 'PT900001', NULL, 'Payment', '2026-11-07', 300000, 'Cash', 3601, SYSDATETIMEOFFSET()), "
            + "(NEWID(), 'PT900002', NULL, 'Payment', '2026-11-06', 150000, 'VietQrInvoice', 3602, SYSDATETIMEOFFSET()), "
            + "(NEWID(), 'PT900003', NULL, 'Payment', '2026-11-05', 50000, 'Cash', 3603, SYSDATETIMEOFFSET())");
        var before = await PaymentsAsync(db);

        Assert.Equal(3, before.Count);

        await migrator.MigrateAsync();

        Assert.Equal(before, await PaymentsAsync(db));
        Assert.Equal(0, await NewColumnsSetAsync(db));
        Assert.Equal(
            ["IX_CustomerPayments_DeduplicationKey unique=1 filter=([DeduplicationKey] IS NOT NULL)", "IX_CustomerPayments_StatusId_PaymentDate unique=0 filter=-"],
            await IndexesAsync(db));

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(before, await PaymentsAsync(db));
        Assert.Equal(["IX_CustomerPayments_StatusId unique=0 filter=-"], await IndexesAsync(db));

        await migrator.MigrateAsync();
        Assert.Equal(before, await PaymentsAsync(db));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
