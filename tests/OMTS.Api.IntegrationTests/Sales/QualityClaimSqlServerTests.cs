using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-01 and CLAIM-02 on a real SQL Server: the filtered unique index, row versions and parallel requests answer 2xx or 409, never 500.</summary>
public class QualityClaimSqlServerTests
{
    private const int Rounds = 4;

    private sealed record World(FarmingSqlServerHost Host, HttpClient Owner, TraderActor Trader, Guid ProductId, Guid[] LotIds);

    private static async Task<World> StartAsync(int invoices = 1)
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var lots = new Guid[invoices];
        for (var i = 0; i < invoices; i++)
        {
            lots[i] = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(i % 5 + 1))).Id();
        }

        return new World(host, owner, await PurchaseRequestSqlServerTests.TraderOnAsync(host), product.Id(), lots);
    }

    private static async Task<Guid> SellAsync(World w, int lot) =>
        (await w.Owner.SellAsync(Invoice(w.Trader.Profile.Id, [Line(w.ProductId, w.LotIds[lot], 10m)], Cash(300_000m), w.LotIds[lot]))).GetProperty("invoice").Id();

    private static Task<string> RowVersionAsync(World w, Guid claimId) =>
        w.Owner.ClaimAsStaffAsync(claimId).ContinueWith(t => t.Result.Text("rowVersion")!);

    private static QualityClaim NewClaim(World w, Guid invoiceId, string code) =>
        new() { Code = code, InvoiceId = invoiceId, CustomerId = w.Trader.Profile.Id, SentAt = DateTimeOffset.UtcNow, Description = "x" };

    [SqlServerFact]
    public async Task The_filtered_unique_index_allows_one_claim_awaiting_per_invoice_and_new_ones_after_an_answer()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var invoice = await SellAsync(w, 0);
        await w.Host.UseDbAsync(db => db.QualityClaims.Add(NewClaim(w, invoice, "KN900001")));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => w.Host.UseDbAsync(db => db.QualityClaims.Add(NewClaim(w, invoice, "KN900002"))));
        Assert.Equal(2601, ((SqlException)duplicate.InnerException!).Number);

        await w.Host.UseDbAsync(db =>
        {
            var claim = db.QualityClaims.Single(c => c.InvoiceId == invoice);
            claim.StatusId = StatusIds.QualityClaim.Rejected;
            claim.ResponseNote = "No";
            claim.RespondedAt = DateTimeOffset.UtcNow;
            claim.RespondedByAccountId = w.Host.Owner.Account.Id;
        });
        await w.Host.UseDbAsync(db => db.QualityClaims.Add(NewClaim(w, invoice, "KN900003")));
        Assert.Equal(2, await w.Host.Api.DbAsync(db => db.QualityClaims.CountAsync(c => c.InvoiceId == invoice)));
    }

    [SqlServerFact]
    public async Task A_rejected_claim_without_a_note_or_an_accepted_one_without_an_offer_is_refused_by_the_database()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var invoice = await SellAsync(w, 0);
        var claim = NewClaim(w, invoice, "KN900010");
        await w.Host.UseDbAsync(db => db.QualityClaims.Add(claim));
        await Assert.ThrowsAsync<DbUpdateException>(() => w.Host.UseDbAsync(db =>
        {
            var row = db.QualityClaims.Single(c => c.Id == claim.Id);
            row.StatusId = StatusIds.QualityClaim.Rejected;
            row.RespondedAt = DateTimeOffset.UtcNow;
            row.RespondedByAccountId = w.Host.Owner.Account.Id;
        }));
        await Assert.ThrowsAsync<DbUpdateException>(() => w.Host.UseDbAsync(db =>
        {
            var row = db.QualityClaims.Single(c => c.Id == claim.Id);
            row.StatusId = StatusIds.QualityClaim.Accepted;
            row.RespondedAt = DateTimeOffset.UtcNow;
            row.RespondedByAccountId = w.Host.Owner.Account.Id;
        }));
    }

    // Two sends of one trader meet: one claim stands, the other gets 409 and leaves no photo behind.
    [SqlServerFact]
    public async Task A_parallel_double_submit_on_an_invoice_saves_one_claim_and_keeps_only_its_photo()
    {
        var w = await StartAsync(Rounds);
        await using var _ = w.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SellAsync(w, round);

            var answers = await InParallelAsync(2, _ => w.Trader.Client.PostClaimAsync(invoice));

            await AssertAnswersAsync(answers, "201", "409 MSG06", "409 MSG-GEN-09");
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
            Assert.Equal(1, await w.Host.Api.DbAsync(db => db.QualityClaims.CountAsync(c => c.InvoiceId == invoice)));
            var claimId = await w.Host.Api.DbAsync(db => db.QualityClaims.Where(c => c.InvoiceId == invoice).Select(c => c.Id).SingleAsync());
            Assert.Equal(1, await w.Host.Api.DbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.QualityClaimNew && n.RelatedRecordId == claimId)));
        }

        Assert.Equal(Rounds, w.Host.StoredKeys(FileCategories.QualityClaimPhotos).Count);
    }

    // CLAIM-02: two taps on Accept, or Accept against Reject: exactly one response, one message, and the row version guards the other.
    [SqlServerFact]
    public async Task A_parallel_double_respond_gives_exactly_one_success_one_response_and_one_message()
    {
        var w = await StartAsync(Rounds);
        await using var _ = w.Host;
        var sms = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var claim = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, round));
            var rowVersion = await RowVersionAsync(w, claim.Id());

            var answers = await InParallelAsync(2, i => i == 0 ? w.Owner.AcceptClaimAsync(claim.Id(), rowVersion) : w.Owner.RejectClaimAsync(claim.Id(), rowVersion));

            // The view has a "code" of its own, so the 200 is read by status and only the loser by its code.
            await AssertAnswersAsync(answers.Where(a => a.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            var status = (await w.Host.Api.DbAsync(db => db.QualityClaims.AsNoTracking().SingleAsync(c => c.Id == claim.Id()))).StatusId;
            Assert.Equal(answers[0].StatusCode == HttpStatusCode.OK ? StatusIds.QualityClaim.Accepted : StatusIds.QualityClaim.Rejected, status);
            var rows = await w.Host.Api.NotificationRowsAsync(claim.Id());
            Assert.Single(rows.InList().OfType(NotificationTypes.QualityClaimResponse));
            var sending = Assert.Single(rows.Sendings());
            Assert.Equal(StatusIds.MessageDelivery.Sent, sending.DeliveryStatusId);
            sms++;
            Assert.Equal(sms, w.Host.Api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(w.Trader.Profile.PhoneNumber!).Count);
        }
    }

    [SqlServerFact]
    public async Task A_stale_form_answers_msg06_and_a_response_does_not_touch_the_invoice_row()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var invoiceId = await SellAsync(w, 0);
        var claim = await w.Trader.Client.SubmitClaimAsync(invoiceId);
        var rowVersion = await RowVersionAsync(w, claim.Id());

        var stale = await w.Owner.AcceptClaimAsync(claim.Id(), Convert.ToBase64String(new byte[8]));
        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (stale.StatusCode, await stale.ErrorCodeAsync()));

        var invoiceBefore = await w.Host.Api.DbAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId));
        await (await w.Owner.AcceptClaimAsync(claim.Id(), rowVersion, "PriceCutWholeInvoice")).ReadAsync(HttpStatusCode.OK);
        var invoiceAfter = await w.Host.Api.DbAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId));
        Assert.Equal(invoiceBefore.RowVersion, invoiceAfter.RowVersion);
    }

    [SqlServerFact]
    public async Task A_trader_reads_the_claim_from_a_migrated_database()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var claim = await w.Trader.Client.SubmitClaimAsync(await SellAsync(w, 0));

        var detail = await (await w.Trader.Client.GetAsync($"/api/me/quality-claims/{claim.Id()}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(claim.Text("code"), detail.Text("code"));
        Assert.Equal(1, await w.Host.Api.DbAsync(db => db.QualityClaimPhotos.CountAsync(p => p.ClaimId == claim.Id())));
    }
}
