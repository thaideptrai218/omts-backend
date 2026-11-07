using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// SALE-06 and the end of the sale on SQL Server: parallel ends of one sale and parallel Resends with real row versions, the DEBT-01 count of the codes already sent, and the
/// migration. Every answer of a race is a 200 or a 409, never a 500.
/// </summary>
public class SalesInvoiceSendingSqlServerTests
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid RaftId, Guid ProductId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, raftId, product.Id());
    }

    private static async Task<JsonElement> SellAsync(Setup setup, Guid customerId, decimal cash = 300_000m, string rest = "None")
    {
        var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1));
        var body = Invoice(customerId, [Line(setup.ProductId, lot.Id(), 10m)], Cash(cash, rest, payLaterOwnerAgreed: rest == "PayLater"), lot.Id());
        return (await setup.Manager.SellAsync(body)).GetProperty("invoice");
    }

    private static Task<List<Notification>> SendingsAsync(FarmingSqlServerHost host, Guid invoiceId) =>
        host.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == invoiceId && n.Channel != NotificationChannel.InApp).ToListAsync());

    // Once only (QrDecidedAt): the Zone Manager and the Owner end the same sale at once; both are answered 200 with the one decision (the second end of the sale answers the
    // current result), and one message goes out.
    [SqlServerFact]
    public async Task Two_parallel_ends_of_one_sale_finish_it_once_and_send_it_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var customerId = (await setup.Owner.CreateCustomerAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SellAsync(setup, customerId);

            var answers = await Task.WhenAll(setup.Manager.FinishAsync(invoice.Id(), true), setup.Owner.FinishAsync(invoice.Id(), false));

            var bodies = await Task.WhenAll(answers.Select(a => a.ReadAsync(HttpStatusCode.OK)));
            Assert.Single(bodies.Select(b => b.GetProperty("invoice").Text("qrDecidedAt")).Distinct());
            Assert.Single(await SendingsAsync(setup.Host, invoice.Id()));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == invoice.Id() && e.Action == "SalesInvoice.SaleFinished")));
        }
    }

    // PRE-2 under a race: two Resends at once while the first is still with the service; the other reads Sending and is refused, so one more message goes out, not two.
    [SqlServerFact]
    public async Task Two_parallel_resends_send_once_while_the_first_is_with_the_service()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var sender = new HeldSender();
        await using var held = setup.Host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ITraderMessageSender>(sender)));
        var owner = await LoginAsync(held, setup.Host.Owner.Account.Username);
        var customerId = (await setup.Owner.CreateCustomerAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SellAsync(setup, customerId);
            await setup.Manager.FinishedAsync(invoice.Id(), wantsQrCode: false);
            sender.Reset();

            var first = owner.ResendAsync(invoice.Id());
            var second = owner.ResendAsync(invoice.Id());
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var refused = await Task.WhenAny(first, second).Unwrap();
            sender.Release.TrySetResult();
            var answers = await Task.WhenAll(first, second);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-GEN-09", (await refused.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Equal(1, sender.Calls);
            var rows = await SendingsAsync(setup.Host, invoice.Id());
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(StatusIds.MessageDelivery.Sent, r.DeliveryStatusId));
        }
    }

    // BR-DEBT-01: before the account changes, the unpaid invoices whose accepted message named the current account (ISentInvoiceQrCodeQuery feeds MSG-DEBT-01).
    [SqlServerFact]
    public async Task The_count_of_codes_already_sent_is_the_unpaid_invoices_whose_accepted_message_named_the_current_account()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        await setup.Host.UseDbAsync(db => db.Cooperatives.Single().BankAccount = new ReceivingBankAccount
        {
            BankBin = "970000", BankName = SalesInvoiceSendingTestKit.DemoBankName, AccountNumber = SalesInvoiceSendingTestKit.DemoAccountNumber, AccountHolderName = "DEMO", AcceptedAt = DateTimeOffset.UtcNow,
        });
        var debtor = await setup.Owner.CreateCustomerAsync();
        var refused = await setup.Owner.CreateCustomerAsync();
        var unpaid = await SellAsync(setup, debtor.Id(), cash: 100_000m, rest: "PayLater");
        var paid = await SellAsync(setup, debtor.Id());
        var notAccepted = await SellAsync(setup, refused.Id(), cash: 0m, rest: "PayLater");
        var notSentYet = await SellAsync(setup, debtor.Id(), cash: 0m, rest: "PayLater");
        // Nothing to send (no phone): the Not sent row names no account and is not counted.
        var noPhone = await setup.Owner.CreateCustomerAsync();
        var nothingSent = await SellAsync(setup, noPhone.Id(), cash: 0m, rest: "PayLater");
        await setup.Host.UseDbAsync(db => db.Customers.Single(c => c.Id == noPhone.Id()).PhoneNumber = null);
        setup.Host.Api.RefuseSmsTo(refused.Text("phoneNumber")!);
        foreach (var invoice in new[] { unpaid, paid, notAccepted, nothingSent })
        {
            await setup.Manager.FinishedAsync(invoice.Id(), wantsQrCode: false);
        }

        Assert.Equal(1, await CountAsync(setup.Host));
        Assert.NotNull(notSentYet.Text("code"));
        var nothingSentRow = Assert.Single(await SendingsAsync(setup.Host, nothingSent.Id()));
        Assert.Equal(((int?)StatusIds.MessageDelivery.NotSent, (string?)null),(nothingSentRow.DeliveryStatusId, nothingSentRow.PhoneNumber));

        // A message sent with another account does not count for the current one.
        await setup.Host.UseDbAsync(db => db.Cooperatives.Single().BankAccount!.AccountNumber = "999900000009");
        Assert.Equal(0, await CountAsync(setup.Host));
    }

    [SqlServerFact]
    public async Task The_migration_goes_down_over_a_sending_without_recipient_and_up_again_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_SalesInvoices_QrToken' AND is_unique = 1 AND has_filter = 1").SingleAsync());
        db.Notifications.Add(new Notification { Channel = NotificationChannel.Sms, Type = "SALES_INVOICE_MESSAGE", Title = "Sales invoice", CreatedAt = DateTimeOffset.UtcNow, PhoneNumber = "999900000003" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync("20261107112721_SalesInvoices");

        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Notifications").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('SalesInvoices') AND name IN ('QrToken', 'QrDecidedAt', 'QrCreatedAt')").SingleAsync());

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Notifications') AND name = 'RecipientAccountId' AND is_nullable = 1").SingleAsync());
    }

    private static async Task<int> CountAsync(FarmingSqlServerHost host)
    {
        using var scope = host.Api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISentInvoiceQrCodeQuery>().CountUnpaidInvoicesWithSentCodesAsync(CancellationToken.None);
    }

    private static async Task<HttpClient> LoginAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string username)
    {
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>Holds each message until released (well within the 10 seconds a send may take), so a sending stays Sending while the other request runs.</summary>
    private sealed class HeldSender : ITraderMessageSender
    {
        private int _calls;

        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => _calls;

        public NotificationChannel Channel => NotificationChannel.Sms;

        public void Reset()
        {
            _calls = 0;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return DeliveryResult.Sent("held");
        }
    }
}
