using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223, SALE-04.3: refusals, preview against merge, messages, audit.</summary>
public class SalesCatalogueQaMergeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<HttpClient> ManagerAsync() => await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

    public static TheoryData<string> Malformed => new()
    {
        "", "null", "[]", "{", "{\"mergedCustomerId\":\"abc\"}", "{\"mergedCustomerId\":123}", "{\"ownerApproved\":\"yes\"}", "{\"ownerApproved\":null}", "{\"keptRowVersion\":1}", "{\"mergedRowVersion\":[]}", "{}",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_merge_bodies_are_client_errors_and_nothing_merges(string body)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();

        var response = await owner.PostAsync($"/api/customers/{kept.Id()}/merge", Json(body));

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("mergedCustomerId=abc")]
    [InlineData("mergedCustomerId=")]
    [InlineData("mergedCustomerId=00000000-0000-0000-0000-000000000000")]
    public async Task A_preview_with_a_bad_profile_id_is_a_client_error(string query)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();

        var response = await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview?{query}");

        Assert.True((int)response.StatusCode is >= 400 and < 500, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Unreadable_versions_are_msg_gen_01_on_each_side()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        Assert.Equal("RowVersion", await (await owner.PostMergeAsync(kept.Id(), merged.Id(), "!!!", merged.RowVersion())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("RowVersion", await (await owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), "!!!")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(JsonValueKind.Null, (await owner.CustomerAsync(merged.Id())).GetProperty("mergedIntoCustomerId").ValueKind);
    }

    // The refusals name both profiles; the order of the checks never leaks a merge through.
    [Fact]
    public async Task A_zone_manager_saying_no_changes_nothing_and_the_preview_writes_nothing()
    {
        var manager = await ManagerAsync();
        var kept = await manager.CreateCustomerAsync();
        var merged = await manager.CreateCustomerAsync();
        var auditBefore = await factory.QueryAsync(db => db.AuditEvents.CountAsync());

        var preview = await (await manager.GetAsync($"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={merged.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.True(preview.GetProperty("requiresOwnerApproval").GetBoolean());
        await (await manager.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), merged.RowVersion(), ownerApproved: false)).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-04");

        Assert.Equal(auditBefore, await factory.QueryAsync(db => db.AuditEvents.CountAsync()));
        Assert.Equal(JsonValueKind.Null, (await manager.CustomerAsync(merged.Id())).GetProperty("mergedIntoCustomerId").ValueKind);
    }

    // MSG-SALE-04 with a kept profile that has no number (registered with an email): the text still reads.
    [Fact]
    public async Task The_confirmation_reads_when_the_kept_profile_has_no_number()
    {
        var manager = await ManagerAsync();
        var (_, kept) = await factory.LinkedProfileAsync(name: "Kept", configure: c => c.PhoneNumber = null);
        var merged = await manager.CreateCustomerAsync(name: "Merged", phone: NewPhone());

        var preview = await (await manager.GetAsync($"/api/customers/{kept.Id}/merge-preview?mergedCustomerId={merged.Id()}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(
            $"Has the Cooperative Owner agreed to merge Merged ({merged.Text("phoneNumber")}, debt 0 VND) into Kept (–, debt 0 VND)? The merge cannot be undone.",
            preview.Text("confirmation"));
    }

    // Part V: MSG-GEN-02, MSG-GEN-09 (Merged is worked out, I:539), MSG06 with what the screen needs to read again.
    [Fact]
    public async Task The_customer_message_texts_follow_the_srs_list()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var kept = await owner.CreateCustomerAsync(name: "Kept");
        var merged = await owner.CreateCustomerAsync(name: "Merged", phone: phone);

        var saved = await (await owner.PostAsJsonAsync("/api/customers", new { name = "New", phoneNumber = NewPhone() })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("Customer profile saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        var duplicate = await (await owner.PostAsJsonAsync("/api/customers", new { name = "Copy", phoneNumber = phone })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal("Customer profile already exists. Your entries are kept: change them, or open the existing record.", duplicate.Text("detail"));
        var opened = await owner.CustomerAsync(duplicate.GetProperty("existingId").GetGuid());
        Assert.Equal(phone, opened.Text("phoneNumber"));

        var stale = await (await owner.PostMergeAsync(kept.Id(), merged.Id(), Convert.ToBase64String(Guid.NewGuid().ToByteArray()), merged.RowVersion())).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.StartsWith("The data was just updated by ", stale.Text("detail"));
        Assert.True(stale.TryGetProperty("currentRowVersion", out _));

        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);
        var edit = await owner.PutAsJsonAsync($"/api/customers/{merged.Id()}", new { name = "X", phoneNumber = phone, rowVersion = merged.RowVersion() });
        Assert.Equal("Customer profile Merged is Merged and cannot be used for this action.", (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09")).Text("detail"));
    }

    // GBR-02: who, when, before and after of a merge a Zone Manager did with the Owner's agreement.
    [Fact]
    public async Task The_merge_audit_row_holds_the_actor_and_the_values_of_both_profiles()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var manager = await ManagerAsync();
        var kept = await owner.CreateCustomerAsync(name: "Kept");
        var merged = await owner.CreateCustomerAsync(name: "Merged");

        await (await manager.MergeAsync(kept.Id(), merged.Id(), ownerApproved: true)).ReadAsync(HttpStatusCode.OK);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Customer.Merged" && e.RecordId == merged.Id()));
        var (before, after) = (JsonDocument.Parse(audit.Before!).RootElement, JsonDocument.Parse(audit.After!).RootElement);
        Assert.Equal(merged.Text("phoneNumber"), audit.RecordCode);
        Assert.NotNull(audit.ActorAccountId);
        Assert.Equal(("Merged", merged.Text("phoneNumber")), (before.GetProperty("Name").GetString(), before.GetProperty("PhoneNumber").GetString()));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("MergedIntoCustomerId").ValueKind);
        Assert.Equal((kept.Id(), "Kept", kept.Text("phoneNumber")), (after.GetProperty("MergedIntoCustomerId").GetGuid(), after.GetProperty("KeptName").GetString(), after.GetProperty("KeptPhoneNumber").GetString()));
    }

    // A chain of three, one merge at a time: each older row follows and each merge is one audit row of its merged profile.
    [Fact]
    public async Task Three_merges_in_turn_keep_every_row_on_the_last_kept_profile()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var profiles = new List<JsonElement>();
        for (var i = 0; i < 4; i++)
        {
            profiles.Add(await owner.CreateCustomerAsync());
        }

        for (var i = 0; i < 3; i++)
        {
            await (await owner.MergeAsync(profiles[i + 1].Id(), profiles[i].Id())).ReadAsync(HttpStatusCode.OK);
        }

        var last = profiles[3].Id();
        foreach (var profile in profiles.Take(3))
        {
            Assert.Equal(last, (await owner.CustomerAsync(profile.Id())).GetProperty("mergedIntoCustomerId").GetGuid());
            Assert.Single(await factory.AuditActionsAsync(profile.Id()), a => a == "Customer.Merged");
        }
    }

    // SALE-04 step 1: not listed; the number of a merged profile finds only who holds it now.
    [Fact]
    public async Task A_merged_profile_is_not_found_by_its_name_or_number_but_still_opens_read_only()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var phone = NewPhone();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync(name: tag, phone: phone);
        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Empty((await (await owner.GetAsync($"/api/customers?search={tag}")).ReadAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Empty((await (await owner.GetAsync($"/api/customers?search={phone}")).ReadAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        var view = await owner.CustomerAsync(merged.Id());
        Assert.Equal((tag, phone), (view.Text("name"), view.Text("phoneNumber")));
    }
}
