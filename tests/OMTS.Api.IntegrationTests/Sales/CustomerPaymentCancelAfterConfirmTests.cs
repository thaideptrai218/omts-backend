using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// PM answer 12/11/2026 (OMTS-224 [1.6]): a cancellation of a payment found Confirmed, with no row version sent (the client lost a race with a confirmation, or never saw the
/// confirmation), is allowed and writes an audit row saying the payment was Confirmed when it was cancelled. One that sends the row version it showed, or cancels a payment still
/// Awaiting confirmation, writes none.
/// </summary>
public class CustomerPaymentCancelAfterConfirmTests
{
    private static Task<List<AuditEvent>> AuditOfAsync(AuthApiFactory api, Guid paymentId) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == paymentId).OrderBy(e => e.OccurredAt).ToListAsync());

    [Fact]
    public async Task Cancelling_a_confirmed_payment_without_a_row_version_writes_the_cancelled_after_confirm_row()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountant = await api.AllZonesAccountantAsync();
        var cash = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
        await (await accountant.Client.ConfirmAsync([cash])).ReadAsync(HttpStatusCode.OK);

        var answer = await accountant.Client.CancelAsync(cash, "Not handed over");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(StatusIds.CustomerPayment.Cancelled, await api.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == cash).Select(p => p.StatusId).SingleAsync()));
        var audit = await AuditOfAsync(api, cash);
        var code = await api.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == cash).Select(p => p.Code).SingleAsync());
        Assert.Single(audit, e => e.Action == CustomerPaymentStatusChanges.CancelledAction);
        var raced = Assert.Single(audit, e => e.Action == CustomerPaymentStatusChanges.CancelledAfterConfirmAction);
        Assert.Equal("CustomerPayment.CancelledAfterConfirm", raced.Action);
        Assert.Equal((code, "Not handed over", accountant.Account.Id), (raced.RecordCode, raced.Reason, raced.ActorAccountId));
        // The row says what the payment was: Confirmed, by whom and when; and what it became.
        Assert.Contains("COMPLETED", raced.Before!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(accountant.Account.Id.ToString(), raced.Before, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CANCELLED", raced.After!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancellation_of_a_payment_still_awaiting_confirmation_or_with_the_row_version_shown_writes_no_such_row()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountant = await api.AllZonesAccountantAsync();
        var awaiting = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
        var recorded = (await accountant.Client.PaidAsync(MoneyBody(sale.CustomerId, 10_000m))).Payment();

        Assert.Equal(HttpStatusCode.OK, (await accountant.Client.CancelAsync(awaiting, "Not handed over")).StatusCode);
        // The Confirmed payment was read by the client, which sends the version it showed: it knows what it cancels.
        // InMemory generates no row versions: one is stored on the row, as SQL Server would, and sent back.
        var shown = await api.IssueRowVersionAsync<CustomerPayment>(recorded.Id());
        var cancelled = await accountant.Client.PostAsJsonAsync($"/api/customer-payments/{recorded.Id()}/cancel", new { reason = "Recorded by mistake", rowVersion = shown });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Empty((await AuditOfAsync(api, awaiting)).Where(e => e.Action == CustomerPaymentStatusChanges.CancelledAfterConfirmAction));
        Assert.Empty((await AuditOfAsync(api, recorded.Id())).Where(e => e.Action == CustomerPaymentStatusChanges.CancelledAfterConfirmAction));
        Assert.Single((await AuditOfAsync(api, recorded.Id())).Where(e => e.Action == CustomerPaymentStatusChanges.CancelledAction));
    }
}
