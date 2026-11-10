using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of who can do what on quality claims (CLAIM-01, CLAIM-02, BR-ACCOUNT-04, §I.4.4).</summary>
public class QualityClaimQaAccessTests
{
    private static async Task<(HttpClient Client, Account Account)> StaffAsync(AuthApiFactory api, params (StaffRole Role, FarmingZone? Zone)[] pairs)
    {
        var password = NewPassword();
        var account = await api.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        foreach (var (role, zone) in pairs)
        {
            await api.GrantAsync(account, role, zone);
        }

        return (await api.LoginAsync(account.Username, password), account);
    }

    private static Task<FarmingZone> ZoneAsync(AuthApiFactory api, Guid zoneId) => api.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == zoneId));

    [Fact]
    public async Task Anonymous_callers_get_401_on_every_claim_endpoint()
    {
        using var api = new QualityClaimApiFactory();
        var anonymous = api.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/quality-claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/me/quality-claims/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostClaimAsync(id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/quality-claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/quality-claims/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.AcceptClaimAsync(id, "AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.RejectClaimAsync(id, "AAAA")).StatusCode);
    }

    [Fact]
    public async Task A_locked_trader_can_neither_send_nor_read_a_claim()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var second = await c.World.Sale.SellToAsync(c.World.Trader.Profile.Id, c.World.Sale.OtherLotId);

        await api.UpdateAsync<Account>(c.World.Trader.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        Assert.Contains((await c.World.Trader.Client.PostClaimAsync(second)).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await c.World.Trader.Client.GetAsync($"/api/me/quality-claims/{c.Id}")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(1, await api.QueryAsync(db => db.QualityClaims.CountAsync()));
    }

    [Fact]
    public async Task A_locked_owner_cannot_respond()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();

        await api.UpdateAsync<Account>(c.World.Sale.Owner.Account.Id, a => a.StatusId = StatusIds.Account.Locked);

        Assert.Contains((await c.Owner.AcceptClaimAsync(c.Id, c.RowVersion)).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(StatusIds.QualityClaim.AwaitingResponse, await api.QueryAsync(db => db.QualityClaims.Where(q => q.Id == c.Id).Select(q => q.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_trader_account_without_a_profile_is_refused_on_submit_and_sees_nothing()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var unlinked = await FarmingTestKit.TraderAsync(api);

        Assert.Equal(HttpStatusCode.Forbidden, (await unlinked.PostClaimAsync(c.World.InvoiceId)).StatusCode);
        var mine = await (await unlinked.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-11", 0), (mine.Text("messageCode"), mine.GetProperty("total").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await unlinked.GetAsync($"/api/me/quality-claims/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_invoice_or_claim_of_another_trader_answers_exactly_like_an_unknown_id()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var other = await api.TraderAsync();

        var foreignInvoice = await other.Client.PostClaimAsync(c.World.InvoiceId);
        var unknownInvoice = await other.Client.PostClaimAsync(Guid.NewGuid());
        var foreignClaim = await other.Client.GetAsync($"/api/me/quality-claims/{c.Id}");
        var unknownClaim = await other.Client.GetAsync($"/api/me/quality-claims/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreignInvoice.StatusCode);
        Assert.Equal(unknownInvoice.StatusCode, foreignInvoice.StatusCode);
        Assert.Equal(await unknownInvoice.Content.ReadAsStringAsync(), await foreignInvoice.Content.ReadAsStringAsync());
        Assert.Equal(unknownClaim.StatusCode, foreignClaim.StatusCode);
        Assert.Equal(await unknownClaim.Content.ReadAsStringAsync(), await foreignClaim.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_answered_or_cancelled_invoice_of_another_trader_still_answers_404_not_its_status()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        await api.UseDbAsync(db => db.SalesInvoices.Where(i => i.Id == world.InvoiceId).ToList().ForEach(i => i.StatusId = StatusIds.SalesInvoice.Cancelled));
        var other = await api.TraderAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostClaimAsync(world.InvoiceId)).StatusCode);
    }

    [Fact]
    public async Task Staff_and_the_owner_cannot_use_the_trader_endpoints()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var accountant = await OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit.AccountantAsync(api);

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Owner.GetAsync("/api/me/quality-claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.World.Sale.Manager.Client.GetAsync($"/api/me/quality-claims/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostClaimAsync(c.World.InvoiceId)).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_responds_and_others_are_refused_before_the_claim_is_looked_up()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var accountant = await OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit.AccountantAsync(api);
        var allZones = await StaffAsync(api, (StaffRole.ZoneManager, null));
        var trader = c.World.Trader.Client;
        var unknown = Guid.NewGuid();

        foreach (var caller in new[] { c.World.Sale.Manager.Client, accountant, trader, allZones.Client })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.AcceptClaimAsync(c.Id, c.RowVersion)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.RejectClaimAsync(c.Id, c.RowVersion)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.AcceptClaimAsync(unknown, c.RowVersion)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await c.Owner.AcceptClaimAsync(unknown, c.RowVersion)).StatusCode);
        Assert.Equal(StatusIds.QualityClaim.AwaitingResponse, await api.QueryAsync(db => db.QualityClaims.Where(q => q.Id == c.Id).Select(q => q.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task An_accountant_of_every_zone_and_a_manager_of_another_zone_get_no_claims()
    {
        using var api = new QualityClaimApiFactory();
        var c = await api.QaClaimedAsync();
        var other = await api.ClaimWorldAsync();
        var accountant = await StaffAsync(api, (StaffRole.Accountant, null));

        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.GetAsync("/api/quality-claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.GetAsync($"/api/quality-claims/{c.Id}")).StatusCode);

        var outsider = other.Sale.Manager.Client;
        var list = await (await outsider.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(list.Items());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/quality-claims/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/quality-claims/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_zone_is_gone_at_the_next_request_while_the_other_zone_stays_and_all_zones_sees_everything()
    {
        using var api = new QualityClaimApiFactory();
        var a = await api.QaClaimedAsync();
        var b = await api.QaClaimedAsync();
        var zoneA = await ZoneAsync(api, a.World.Sale.ZoneId);
        var zoneB = await ZoneAsync(api, b.World.Sale.ZoneId);
        var staff = await StaffAsync(api, (StaffRole.ZoneManager, zoneA), (StaffRole.ZoneManager, zoneB));
        var everywhere = await StaffAsync(api, (StaffRole.ZoneManager, null));

        var both = await (await staff.Client.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, both.GetProperty("total").GetInt32());
        var all = await (await everywhere.Client.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        (await everywhere.Client.GetAsync($"/api/quality-claims/{a.Id}")).EnsureSuccessStatusCode();

        var grantA = await api.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == staff.Account.Id && g.ZoneId == zoneA.Id).Select(g => g.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await a.Owner.PostAsJsonAsync($"/api/accounts/{staff.Account.Id}/grants/{grantA}/revoke", new { reason = "Moved" })).StatusCode);

        var after = await (await staff.Client.GetAsync("/api/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(b.Id, after.Items().Single().Id());
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.Client.GetAsync($"/api/quality-claims/{a.Id}")).StatusCode);
        (await staff.Client.GetAsync($"/api/quality-claims/{b.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_claim_of_a_profile_merged_into_a_trader_moves_with_the_merge()
    {
        using var api = new QualityClaimApiFactory();
        var world = await api.ClaimWorldAsync();
        var owner = world.Sale.Owner.Client;
        var offline = await owner.CreateCustomerAsync();
        var invoiceOfOffline = await world.Sale.SellToAsync(offline.Id(), world.Sale.OtherLotId);
        var existing = new QualityClaim
        {
            Code = "KN990001",
            InvoiceId = invoiceOfOffline,
            CustomerId = offline.Id(),
            SentAt = DateTimeOffset.UtcNow,
            Description = "Reported before the merge",
            Photos = [new QualityClaimPhoto { SortOrder = 1, FileKey = "claim-photos/2026/11/0123456789abcdef0123456789abcdef.jpg" }],
        };
        await api.UseDbAsync(db => db.QualityClaims.Add(existing));

        (await owner.MergeAsync(world.Trader.Profile.Id, offline.Id())).EnsureSuccessStatusCode();

        var mine = await (await world.Trader.Client.GetAsync("/api/me/quality-claims")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(existing.Id, mine.Items().Single().Id());
        (await world.Trader.Client.GetAsync($"/api/me/quality-claims/{existing.Id}")).EnsureSuccessStatusCode();
        var again = await (await world.Trader.Client.PostClaimAsync(invoiceOfOffline)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", again.Text("code"));
        Assert.Equal(world.Trader.Profile.Id, await api.QueryAsync(db => db.QualityClaims.Where(q => q.Id == existing.Id).Select(q => q.CustomerId).SingleAsync()));

        var rowVersion = (await owner.ClaimAsStaffAsync(existing.Id)).Text("rowVersion");
        (await owner.AcceptClaimAsync(existing.Id, rowVersion)).EnsureSuccessStatusCode();
        var notice = await api.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.RelatedRecordId == existing.Id && n.Channel == NotificationChannel.InApp));
        Assert.Equal(world.Trader.Account.Id, notice.RecipientAccountId);
    }
}
