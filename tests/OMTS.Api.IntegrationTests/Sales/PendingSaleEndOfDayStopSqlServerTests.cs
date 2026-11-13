using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The end-of-day stop of the sales awaiting their VietQR transfer on SQL Server (council of 03/11, PM answer 12/11/2026, OMTS-224 [3.4]): the stop and the bank notification of the
/// same sale meet on its row version. One of them ends the sale and the other sees how it ended; never an invoice and a stop for one code, never a 500.
/// </summary>
public class PendingSaleEndOfDayStopSqlServerTests
{
    private const int Rounds = 3;

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

    // The transfer saves the invoice, the stop ends the sale for good: whichever is saved first wins and the other finds the sale ended. Either way one end, one outcome.
    [SqlServerFact]
    public async Task A_transfer_racing_the_end_of_day_stop_leaves_the_sale_ended_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var job = setup.Host.Api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>();
        for (var round = 0; round < Rounds; round++)
        {
            var code = await AwaitingAsync(setup, round + 1);

            var transfer = TransferAsync(setup, code, 300_000m, NewReference());
            var stop = job.RunOnceAsync(Today.AddDays(1));
            await Task.WhenAll(transfer, stop);

            var outcome = await OutcomeAsync(await transfer);
            var run = await stop;
            var (status, invoices, stops) = await setup.Host.QueryAsync(async db => (
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync(),
                await db.SalesInvoices.CountAsync(i => i.Code == code),
                await db.AuditEvents.CountAsync(e => e.Action == PendingSaleWriter.StoppedAction && e.RecordCode == code && e.ActorAccountId == null)));
            if (status == PendingSaleStatus.Stopped)
            {
                // The stop won: the transfer is kept without a customer, no invoice.
                Assert.Equal(("WithoutCustomer", 0, 1, 1), (outcome, invoices, stops, run.Stopped));
            }
            else
            {
                // The transfer won: the sale is Completed with its invoice and the stop found it ended.
                Assert.Equal((PendingSaleStatus.Completed, "CompletedSale", 1, 0, 0), (status, outcome, invoices, stops, run.Stopped));
            }
        }
    }

    // Two runs of the job at once (two app instances, or a catch-up beside the daily run) stop the sale once: one audit row, no error.
    [SqlServerFact]
    public async Task Two_runs_at_once_stop_the_sale_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var job = setup.Host.Api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>();
        for (var round = 0; round < Rounds; round++)
        {
            var code = await AwaitingAsync(setup, round + 1);

            var runs = await Task.WhenAll(job.RunOnceAsync(Today.AddDays(1)), job.RunOnceAsync(Today.AddDays(1)));

            Assert.Equal(1, runs.Sum(r => r.Stopped));
            var (status, stops) = await setup.Host.QueryAsync(async db => (
                await db.PendingSales.Where(s => s.Code == code).Select(s => s.Status).SingleAsync(),
                await db.AuditEvents.CountAsync(e => e.Action == PendingSaleWriter.StoppedAction && e.RecordCode == code)));
            Assert.Equal((PendingSaleStatus.Stopped, 1), (status, stops));
        }
    }
}
