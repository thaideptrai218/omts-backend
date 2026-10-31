using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>
/// A trader whose Zalo number has no account asks the cooperative for one from the Mini App; the Cooperative
/// Owner approves (account opens, customer profile linked) or refuses with a reason (shown to the trader).
/// </summary>
public class TraderRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Request_waits_for_the_owner_who_is_notified()
    {
        var owner = await OwnerAsync();
        var phone = NewPhone();
        var zaloId = NewCode();

        var sent = await RequestAsync(zaloId, phone, "Trần Văn Thương", "Mua hàu ở Vân Đồn");

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var body = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PENDING_APPROVAL", body.GetProperty("status").GetString());
        Assert.DoesNotContain(phone, body.GetProperty("maskedPhone").GetString());

        var trader = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.PhoneNumber == phone));
        Assert.Equal(SubjectType.Trader, trader.SubjectType);
        Assert.Equal(StatusIds.Account.PendingApproval, trader.StatusId);
        Assert.Equal(zaloId, trader.ZaloAccountId);
        Assert.Equal("Mua hàu ở Vân Đồn", trader.AccessRequestNote);

        // No session while waiting, whether linking again or opening the Mini App again.
        Assert.Equal("ZALO_APPROVAL_PENDING", await (await LinkAsync(zaloId, phone)).ErrorCodeAsync());
        Assert.Equal("ZALO_APPROVAL_PENDING", await (await ZaloLoginAsync(zaloId)).ErrorCodeAsync());
        // Asking again while waiting changes nothing.
        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(zaloId, phone, "Trần Văn Thương", null)).StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.Accounts.CountAsync(a => a.PhoneNumber == phone)));

        var inbox = await (await owner.GetAsync("/api/me/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), n =>
            n.GetProperty("type").GetString() == "TRADER_ACCESS_REQUESTED" && n.GetProperty("relatedRecordCode").GetString() == trader.Username);
        Assert.True(inbox.GetProperty("unreadCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Approval_opens_the_account_and_tells_the_trader()
    {
        var owner = await OwnerAsync();
        var phone = NewPhone();
        var zaloId = NewCode();
        await RequestAsync(zaloId, phone, "Lê Thị Lái", null);
        var accountId = await factory.QueryAsync(db => db.Accounts.Where(a => a.PhoneNumber == phone).Select(a => a.Id).SingleAsync());

        var pending = await (await owner.GetAsync("/api/trader-requests")).Content.ReadFromJsonAsync<JsonElement>();
        var approved = await owner.PostAsync($"/api/trader-requests/{accountId}/approve", null);
        var again = await owner.PostAsync($"/api/trader-requests/{accountId}/approve", null);

        Assert.Contains(pending.EnumerateArray(), r => r.GetProperty("accountId").GetGuid() == accountId && r.GetProperty("displayName").GetString() == "Lê Thị Lái");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("ACTIVE", (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        await again.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        // The trader gets a session from the Mini App, has an own customer profile and the approval notice.
        var login = await ZaloLoginAsync(zaloId);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(await factory.QueryAsync(db => db.Customers.AnyAsync(c => c.AccountId == accountId)));
        var trader = factory.CreateClient();
        trader.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        var inbox = await (await trader.GetAsync("/api/me/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), n => n.GetProperty("type").GetString() == "TRADER_ACCESS_APPROVED");
    }

    [Fact]
    public async Task Refused_request_shows_the_reason_and_can_be_sent_again()
    {
        var owner = await OwnerAsync();
        var phone = NewPhone();
        var zaloId = NewCode();
        await RequestAsync(zaloId, phone, "Phạm Văn Từ", null);
        var accountId = await factory.QueryAsync(db => db.Accounts.Where(a => a.PhoneNumber == phone).Select(a => a.Id).SingleAsync());

        var noReason = await owner.PostAsJsonAsync($"/api/trader-requests/{accountId}/reject", new { reason = " " });
        var rejected = await owner.PostAsJsonAsync($"/api/trader-requests/{accountId}/reject", new { reason = "Không quen biết" });
        var login = await ZaloLoginAsync(zaloId);

        await noReason.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal("REJECTED", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("ZALO_REQUEST_REJECTED", await login.ErrorCodeAsync());
        Assert.Equal("Không quen biết", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        var refusedList = await (await owner.GetAsync("/api/trader-requests?rejected=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(refusedList.EnumerateArray(), r => r.GetProperty("accountId").GetGuid() == accountId);

        // A new request goes back to the Owner, without the old reason.
        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(zaloId, phone, "Phạm Văn Từ", "Tôi là cháu ông Hải")).StatusCode);
        var trader = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == accountId));
        Assert.Equal(StatusIds.Account.PendingApproval, trader.StatusId);
        Assert.Null(trader.RejectionReason);
        Assert.Equal("Tôi là cháu ông Hải", trader.AccessRequestNote);
    }

    [Fact]
    public async Task A_number_with_an_account_is_linked_not_requested()
    {
        var existing = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);

        var response = await RequestAsync(NewCode(), existing.PhoneNumber!, "Someone", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ZALO_ACCOUNT_EXISTS", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Request_needs_a_name()
    {
        var response = await RequestAsync(NewCode(), NewPhone(), "  ", null);

        await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    [Fact]
    public async Task Only_the_cooperative_owner_reviews_requests()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = await factory.LoginAsync(trader.Username);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/trader-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/trader-requests/{trader.Id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Notifications_are_marked_read_one_by_one_or_all_at_once()
    {
        var owner = await OwnerAsync();
        await RequestAsync(NewCode(), NewPhone(), "Một", null);
        await RequestAsync(NewCode(), NewPhone(), "Hai", null);
        var items = (await (await owner.GetAsync("/api/me/notifications?pageSize=2")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        var first = items[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/me/notifications/{first}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/me/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/me/notifications/read-all", null)).StatusCode);

        var after = await (await owner.GetAsync("/api/me/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, after.GetProperty("unreadCount").GetInt32());
        await (await owner.GetAsync("/api/me/notifications?pageSize=1000")).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    /// <summary>A logged-in Cooperative Owner of the test cooperative (approval links the trader's customer profile).</summary>
    private async Task<HttpClient> OwnerAsync()
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password, SubjectType.CooperativeOwner);
        return await factory.LoginAsync(owner.Username);
    }

    private Task<HttpResponseMessage> RequestAsync(string zaloId, string phone, string? displayName, string? note) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/trader-requests", new
        {
            zaloAccessToken = "fake:" + zaloId,
            phoneToken = "fake:" + phone,
            displayName,
            note,
        });

    private Task<HttpResponseMessage> LinkAsync(string zaloId, string phone) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/link", new { zaloAccessToken = "fake:" + zaloId, phoneToken = "fake:" + phone });

    private Task<HttpResponseMessage> ZaloLoginAsync(string zaloId) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/login", new { zaloAccessToken = "fake:" + zaloId });
}
