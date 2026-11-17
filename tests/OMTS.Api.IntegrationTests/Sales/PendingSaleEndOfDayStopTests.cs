using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The sales awaiting their VietQR transfer are stopped at the end of each business day, UTC+7 (council of 03/11, PM answer 12/11/2026, OMTS-224 [3.4]): which sales the job stops,
/// the audit row with the system as the actor, a second run, the order before the daily close check of the harvest lots, and what a late transfer becomes.
/// </summary>
public class PendingSaleEndOfDayStopTests
{
    private static readonly DateOnly Today = FarmingTestKit.Today;

    private static async Task<string> AwaitAsync(SaleSetup sale, decimal kg = 10m) =>
        (await sale.Manager.Client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, kg)], VietQr()))).Code();

    // The cut is the start of the business day in UTC+7: 13/11 begins at 17:00 UTC on 12/11.
    [Fact]
    public void A_business_day_begins_at_midnight_in_vietnam()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 17, 0, 0, TimeSpan.Zero), PendingSaleEndOfDayService.StartOfDay(new DateOnly(2026, 11, 13)));
        Assert.Equal(BusinessClock.UtcOffset, PendingSaleEndOfDayService.StartOfDay(new DateOnly(2026, 11, 13)).Offset);
    }

    [Fact]
    public async Task The_job_stops_only_the_sales_of_a_day_that_ended_and_leaves_the_ones_already_ended()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var overdue = await AwaitAsync(sale);
        var transferred = await AwaitAsync(sale);
        var byHand = await AwaitAsync(sale);
        var changed = await AwaitAsync(sale);
        await (await sale.Manager.Client.ChangePaymentAsync(changed, Cash(300_000m))).ReadAsync(HttpStatusCode.Created);
        await (await sale.Manager.Client.StopAsync(byHand, confirmed: true)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("CompletedSale", (await api.TransferAsync(transferred, 300_000m)).Outcome());
        var job = api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>();

        // The day the sale was made has not ended: nothing is stopped by the run for that day.
        Assert.Equal(new PendingSaleEndOfDayRun(0), await job.RunOnceAsync(Today));
        Assert.Equal(PendingSaleStatus.Awaiting, (await api.PendingRowAsync(overdue)).Status);

        Assert.Equal(new PendingSaleEndOfDayRun(1), await job.RunOnceAsync(Today.AddDays(1)));

        var stopped = await api.PendingRowAsync(overdue);
        Assert.Equal(PendingSaleStatus.Stopped, stopped.Status);
        Assert.Null(stopped.EndedByAccountId);
        Assert.Null(stopped.InvoiceId);
        Assert.NotNull(stopped.EndedAt);
        Assert.Equal(PendingSaleStatus.Completed, (await api.PendingRowAsync(transferred)).Status);
        Assert.Equal(PendingSaleStatus.PaymentChanged, (await api.PendingRowAsync(changed)).Status);
        var byHandRow = await api.PendingRowAsync(byHand);
        Assert.Equal((PendingSaleStatus.Stopped, sale.Manager.Account.Id), (byHandRow.Status, byHandRow.EndedByAccountId));

        // BR-SALE-08: nothing saved on the invoice side for the stopped sale; the sale that was completed keeps its invoice.
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync(i => i.Code == overdue)));
        Assert.Equal(1, await api.QueryAsync(db => db.SalesInvoices.CountAsync(i => i.Code == transferred)));
        Assert.Equal("Stopped", (await sale.Manager.Client.PolledAsync(overdue)).GetProperty("status").GetString());

        // One audit row for the stop of the system (no account, no employee, the reason says why) beside the one of the stop by hand, which keeps its actor.
        var audit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.Action == PendingSaleWriter.StoppedAction).ToListAsync());
        Assert.Equal(2, audit.Count);
        var system = Assert.Single(audit, e => e.RecordCode == overdue);
        Assert.Null(system.ActorAccountId);
        Assert.Null(system.ActorEmployeeId);
        Assert.Equal((PendingSaleWriter.EndOfDayStopReason, sale.ZoneId), (system.Reason, system.ZoneId));
        Assert.Equal(sale.Manager.Account.Id, Assert.Single(audit, e => e.RecordCode == byHand).ActorAccountId);

        // A second run, even for a later day, changes nothing: the sales have ended.
        Assert.Equal(new PendingSaleEndOfDayRun(0), await job.RunOnceAsync(Today.AddDays(1)));
        Assert.Equal(new PendingSaleEndOfDayRun(0), await job.RunOnceAsync(Today.AddDays(5)));
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == PendingSaleWriter.StoppedAction)));
    }

    // BR-SALE-08: payments already received stay as payments without a customer: the transfer that comes after the stop is kept, not lost and not an invoice.
    [Fact]
    public async Task A_transfer_after_the_automatic_stop_is_kept_without_a_customer()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var code = await AwaitAsync(sale);
        await api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>().RunOnceAsync(Today.AddDays(1));
        var reference = NewReference();

        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 300_000m, reference)).Outcome());

        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Null(payment.CustomerId);
        Assert.Equal(code, payment.TransferInvoiceCode);
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync(i => i.Code == code)));
    }

    // GFR-05 runs after the stop: the kg of the sale that was awaiting are leftover like any kg not sold (the close check keeps back only the kg of a sale still awaiting), so a lot
    // closing on the same run records them all. The hosted service of the daily close check does the stop first, also in its catch-up run.
    [Fact]
    public async Task The_stop_runs_before_the_daily_close_check_of_the_harvest_lots()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var code = await AwaitAsync(sale);
        var runAt = new DateTimeOffset(Today.AddDays(60).ToDateTime(new TimeOnly(8, 0)), BusinessClock.UtcOffset);
        var hosted = new HarvestLotAutoCloseHostedService(
            api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>(),
            api.Services.GetRequiredService<HarvestLotAutoCloseJob>(),
            Options.Create(new HarvestLotAutoCloseOptions { CatchUpOnStart = true }),
            new FixedClock(runAt),
            api.Services.GetRequiredService<ILogger<HarvestLotAutoCloseHostedService>>());

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!api.Logs.Lines.Any(l => l.Contains("Harvest lot daily close check closed", StringComparison.Ordinal)))
            {
                Assert.True(DateTime.UtcNow < until, "The catch-up run did not happen within 30 seconds.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        var lines = api.Logs.Lines.ToList();
        var stopLine = lines.FindIndex(l => l.Contains("Pending sale end-of-day stop stopped 1 sales", StringComparison.Ordinal));
        var closeLine = lines.FindIndex(l => l.Contains("Harvest lot daily close check closed", StringComparison.Ordinal));
        Assert.True(stopLine >= 0 && closeLine > stopLine, $"stop at {stopLine}, close at {closeLine}");
        Assert.Equal(PendingSaleStatus.Stopped, (await api.PendingRowAsync(code)).Status);
        var lot = await api.LotRowAsync(sale.LotId);
        Assert.Equal(StatusIds.HarvestLot.Closed, lot.StatusId);
        Assert.Equal(lot.EstimatedKg, lot.LeftoverKg);
    }
}
