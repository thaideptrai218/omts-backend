using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// GFR-08 and AC-07 on SQL Server: the same transfer reported several times at once is recorded once (unique index on the bank reference of VietQR payments), and a
/// transfer racing Stop sale ends the sale one way only. Every answer is a 2xx or a 409, never a 500.
/// </summary>
public class VietQrSaleSqlServerTests
{
    private const int Rounds = 2;
    private const int Parallel = 4;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId);

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
        var customer = await owner.CreateCustomerAsync();
        var manager = await host.StaffInZoneAsync(zoneId);
        await host.UseDbAsync(db =>
        {
            var cooperative = db.Cooperatives.Single(c => c.Id == host.CooperativeId);
            cooperative.BankAccount = new ReceivingBankAccount
            {
                BankBin = BankBin, BankName = "Vietcombank", AccountNumber = AccountNumber, AccountHolderName = "HTX DEMO", AcceptedAt = DateTimeOffset.UtcNow,
            };
        });
        return new Setup(host, manager.Client, raftId, product.Id(), customer.Id());
    }

    private static async Task<HttpResponseMessage> TransferAsync(Setup setup, string code, decimal amount, string reference)
    {
        using var client = setup.Host.Api.CreateClient();
        return await client.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode = code, amount, bankReference = reference });
    }

    private static async Task<string> OutcomeAsync(HttpResponseMessage response) =>
        Assert.Single((await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray()).Outcome();

    [SqlServerFact]
    public async Task The_same_transfer_reported_at_once_several_times_completes_the_sale_once_and_records_one_payment()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await setup.Host.Owner.Client.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(round + 1));
            var code = (await setup.Manager.AwaitTransferAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 400m)], VietQr(4_000_000m)))).Code();
            var reference = NewReference();

            var answers = await Task.WhenAll(Enumerable.Range(0, Parallel).Select(_ => TransferAsync(setup, code, 8_000_000m, reference)));

            var outcomes = new List<string>();
            foreach (var answer in answers)
            {
                outcomes.Add(await OutcomeAsync(answer));
            }

            Assert.Equal(1, outcomes.Count(o => o == "CompletedSale"));
            Assert.All(outcomes.Where(o => o != "CompletedSale"), o => Assert.Equal("AlreadyRecorded", o));
            var (invoices, transfers, payments, status) = await setup.Host.QueryAsync(async db => (
                await db.SalesInvoices.CountAsync(i => i.Code == code),
                await db.CustomerPayments.CountAsync(p => p.BankReference == reference),
                await db.CustomerPayments.CountAsync(p => p.Invoice!.Code == code),
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync()));
            Assert.Equal((1, 1, 2, PendingSaleStatus.Completed), (invoices, transfers, payments, status));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "SalesInvoice.Created" && e.RecordCode == code)));
        }
    }

    [SqlServerFact]
    public async Task A_transfer_racing_stop_sale_either_completes_the_sale_or_becomes_a_payment_without_a_customer()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await setup.Host.Owner.Client.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(round + 3));
            var code = (await setup.Manager.AwaitTransferAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 10m)], VietQr()))).Code();
            var reference = NewReference();

            var transfer = TransferAsync(setup, code, 300_000m, reference);
            var stop = setup.Manager.StopAsync(code, confirmed: true);
            await Task.WhenAll(transfer, stop);

            var outcome = await OutcomeAsync(await transfer);
            var stopAnswer = await stop;
            Assert.True(stopAnswer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, await stopAnswer.Content.ReadAsStringAsync());
            var (status, invoices, payment) = await setup.Host.QueryAsync(async db => (
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync(),
                await db.SalesInvoices.CountAsync(i => i.Code == code),
                await db.CustomerPayments.AsNoTracking().SingleAsync(p => p.BankReference == reference)));
            if (stopAnswer.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(("WithoutCustomer", PendingSaleStatus.Stopped, 0, (Guid?)null), (outcome, status, invoices, payment.CustomerId));
            }
            else
            {
                Assert.Equal(("CompletedSale", PendingSaleStatus.Completed, 1, (Guid?)setup.CustomerId), (outcome, status, invoices, payment.CustomerId));
            }
        }
    }
}
