using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-01 and the rules of BR-CLAIM-01: what a trader may send and on which invoice; the photos stored all or none.</summary>
public class QualityClaimSubmitTests
{
    private const string Category = FileCategories.QualityClaimPhotos;

    private static Task<int> ClaimCountAsync(AuthApiFactory api, Guid invoiceId) => api.QueryAsync(db => db.QualityClaims.CountAsync(c => c.InvoiceId == invoiceId));

    private static Task SetInvoiceStatusAsync(AuthApiFactory api, Guid invoiceId, int statusId) =>
        api.UseDbAsync(db => db.SalesInvoices.Where(i => i.Id == invoiceId).ToList().ForEach(i => i.StatusId = statusId));

    [Fact]
    public async Task A_trader_sends_a_claim_with_photos_saved_as_awaiting_the_owner_told_the_invoice_and_debt_untouched()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var owner = world.Sale.Owner;
        var debtBefore = await owner.Client.CustomerDebtAsync(world.Trader.Profile.Id);
        var invoiceBefore = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == world.InvoiceId));

        var answer = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId, photos: [Jpeg, Png])).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Quality claim saved.", "MSG-GEN-05"), (answer.Text("message"), answer.Text("messageCode")));
        var claim = answer.GetProperty("claim");
        Assert.StartsWith("KN", claim.Text("code"));
        Assert.Equal((StatusIds.QualityClaim.AwaitingResponse, "Awaiting response"), (claim.GetProperty("statusId").GetInt32(), claim.Text("status")));
        Assert.Equal(QualityClaimTestKit.Description, claim.Text("description"));
        var photos = claim.GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal([1, 2], photos.Select(p => p.GetProperty("sortOrder").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync(photos[0].Text("url"))).StatusCode);
        Assert.Equal(2, StoredKeys(api, Category).Count);

        var notice = await api.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.Type == NotificationTypes.QualityClaimNew));
        Assert.Equal((owner.Account.Id, claim.Id(), claim.Text("code")), (notice.RecipientAccountId, notice.RelatedRecordId, notice.RelatedRecordCode));
        Assert.Equal("MSG-CLAIM-01", notice.MessageCode);
        Assert.Contains(QualityClaimTestKit.Description, notice.Body);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "QualityClaim.Submitted" && e.RecordId == claim.Id() && e.ZoneId == world.Sale.ZoneId)));
        var invoiceAfter = await api.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == world.InvoiceId));
        Assert.Equal((invoiceBefore.StatusId, invoiceBefore.Total), (invoiceAfter.StatusId, invoiceAfter.Total));
        Assert.Equal(debtBefore, await owner.Client.CustomerDebtAsync(world.Trader.Profile.Id));
    }

    [Theory]
    [InlineData(null, "Description")]
    [InlineData("   ", "Description")]
    public async Task A_missing_description_is_refused_and_stores_nothing(string? description, string field)
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();

        var answer = await world.Trader.Client.PostClaimAsync(world.InvoiceId, description);

        Assert.Contains(field, await answer.InvalidFieldsAsync());
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await ClaimCountAsync(api, world.InvoiceId));
    }

    [Fact]
    public async Task Photos_are_required_of_the_allowed_formats_and_at_most_ten()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;

        Assert.Equal("Photos", (await (await trader.PostClaimAsync(world.InvoiceId, photos: [])).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal("Photos", (await (await trader.PostClaimAsync(world.InvoiceId, photos: [Pdf])).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal("Photos", (await (await trader.PostClaimAsync(world.InvoiceId, photos: Enumerable.Repeat(Jpeg, 11).ToList())).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        var part = await (await trader.PostClaimAsync(world.InvoiceId, photoField: "picture")).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Photos", "Send each photo in a part named photos."), (part.Text("field"), part.Text("hint")));
        Assert.Empty(StoredKeys(api, Category));

        await trader.SubmitClaimAsync(world.InvoiceId, Enumerable.Repeat(Jpeg, 10).ToList());
        Assert.Equal(10, StoredKeys(api, Category).Count);
    }

    [Fact]
    public async Task Only_an_invoice_of_the_traders_own_profile_can_be_claimed()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var other = await api.TraderAsync();

        await (await other.Client.PostClaimAsync(world.InvoiceId)).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Forbidden, (await world.Sale.Owner.Client.PostClaimAsync(world.InvoiceId)).StatusCode);
        var walkIn = await world.Sale.SellToAsync(null, world.Sale.OtherLotId);
        await (await world.Trader.Client.PostClaimAsync(walkIn)).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, await ClaimCountAsync(api, world.InvoiceId));
    }

    [Fact]
    public async Task A_claim_on_a_cancelled_or_returned_invoice_is_refused_with_msg_gen_09_and_stores_nothing()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        await SetInvoiceStatusAsync(api, world.InvoiceId, StatusIds.SalesInvoice.Cancelled);

        var cancelled = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("is Cancelled", cancelled.Text("detail"));

        var second = await world.Sale.SellToAsync(world.Trader.Profile.Id, world.Sale.OtherLotId);
        api.Returns.Returned.Add(second);
        var returned = await (await world.Trader.Client.PostClaimAsync(second)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("is returned", returned.Text("detail"));
        Assert.Empty(StoredKeys(api, Category));
    }

    [Fact]
    public async Task A_claim_waits_alone_on_its_invoice_and_an_answered_one_does_not_stop_a_new_one()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var trader = world.Trader.Client;
        var first = await trader.SubmitClaimAsync(world.InvoiceId);

        var second = await (await trader.PostClaimAsync(world.InvoiceId)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Contains("Awaiting response", second.Text("detail"));
        Assert.Single(StoredKeys(api, Category));

        (await world.Sale.Owner.Client.RejectClaimAsync(first.Id(), await RowVersionAsync(world, first.Id()))).EnsureSuccessStatusCode();
        await trader.SubmitClaimAsync(world.InvoiceId);
        Assert.Equal(2, await ClaimCountAsync(api, world.InvoiceId));
    }

    private static async Task<string?> RowVersionAsync(ClaimWorld world, Guid claimId) => (await world.Sale.Owner.Client.ClaimAsStaffAsync(claimId)).Text("rowVersion");

    [Fact]
    public async Task A_photo_that_cannot_be_uploaded_saves_nothing_tells_nobody_and_removes_the_ones_before_it()
    {
        using var api = new FileFaultApiFactory();
        var world = await api.ClaimWorldAsync();
        api.Faults.UploadsBeforeFailure = 1;

        var answer = await (await world.Trader.Client.PostClaimAsync(world.InvoiceId, photos: [Jpeg, Png])).ProblemAsync(HttpStatusCode.BadGateway, "MSG-GEN-03");

        Assert.Equal("MSG05", answer.Text("legacyCode"));
        Assert.Equal(0, await ClaimCountAsync(api, world.InvoiceId));
        Assert.Empty(StoredKeys(api, Category));
        Assert.Equal(0, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.QualityClaimNew)));
    }

    [Fact]
    public async Task A_save_that_conflicts_after_the_uploads_removes_them_and_the_next_send_works()
    {
        using var api = new FileFaultApiFactory();
        var world = await api.ClaimWorldAsync();
        api.Faults.FailNextSave(new ConcurrencyConflictException(new InvalidOperationException("Test: changed by another request.")));

        var answer = await world.Trader.Client.PostClaimAsync(world.InvoiceId);

        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        Assert.Empty(StoredKeys(api, Category));
        await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
    }

    [Fact]
    public async Task A_trader_claims_an_invoice_of_a_profile_merged_into_theirs_and_sees_the_claim()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var owner = world.Sale.Owner.Client;
        var mergedProfile = await owner.CreateCustomerAsync();
        var invoiceOfMerged = await world.Sale.SellToAsync(mergedProfile.Id(), world.Sale.OtherLotId);

        (await owner.MergeAsync(world.Trader.Profile.Id, mergedProfile.Id())).EnsureSuccessStatusCode();

        var claim = await world.Trader.Client.SubmitClaimAsync(invoiceOfMerged);
        var mine = await (await world.Trader.Client.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(mine.GetProperty("items").EnumerateArray(), r => r.Id() == claim.Id());
    }

    [Fact]
    public async Task Claim_photos_are_in_the_orphan_clean_up_and_the_referenced_ones_stay()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
        var kept = Assert.Single(StoredKeys(api, Category));

        var validated = FileValidator.Validate([new IncomingFile("orphan.jpg", Jpeg.Length, new MemoryStream(Jpeg))], FileRules.Photos);
        var orphan = await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], Category);
        Assert.True(orphan.Succeeded);

        await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2));

        Assert.Equal([kept], StoredKeys(api, Category));
    }

    [Fact]
    public void The_claim_categories_and_statuses_exist()
    {
        Assert.Contains(FileCategories.QualityClaimPhotos, OrphanFileCleanupService.Categories);
        Assert.Equal(3701, new QualityClaim().StatusId);
    }
}
