using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-04.3.E1 and the other refusals of a merge: nothing changes.</summary>
public class CustomerMergeRefusalTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<HttpClient> ManagerAsync() => await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

    private Task<Guid?> MergedIntoAsync(Guid id) =>
        factory.QueryAsync(db => db.Customers.Where(c => c.Id == id).Select(c => c.MergedIntoCustomerId).SingleAsync());
    // MSG-SALE-05 also when the Owner agreed: a merge never moves the link of an account.
    [Fact]
    public async Task A_merged_profile_with_a_trader_account_is_refused_keep_it_instead()
    {
        var manager = await ManagerAsync();
        var kept = await manager.CreateCustomerAsync(name: "Offline");
        var (_, linked) = await factory.LinkedProfileAsync(name: "Linked");

        var body = await (await manager.MergeAsync(kept.Id(), linked.Id, ownerApproved: true)).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-05");

        Assert.Equal("Linked cannot be merged into Offline: keep Linked instead, so that its trader account keeps its data.", body.Text("detail"));
        Assert.True(body.GetProperty("canKeepMergedInstead").GetBoolean());
        Assert.Null(await MergedIntoAsync(linked.Id));
    }

    [Fact]
    public async Task Two_profiles_with_trader_accounts_are_not_merged()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, first) = await factory.LinkedProfileAsync(name: "First");
        var (_, second) = await factory.LinkedProfileAsync(name: "Second");

        var body = await (await owner.MergeAsync(first.Id, second.Id)).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-05");

        Assert.Equal("Second cannot be merged into First: both have a trader account.", body.Text("detail"));
        Assert.False(body.GetProperty("canKeepMergedInstead").GetBoolean());
    }

    [Fact]
    public async Task Merging_a_profile_into_itself_or_nothing_returns_MSG_GEN_01_and_an_unknown_one_404()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var customer = await owner.CreateCustomerAsync();

        var self = await owner.PostMergeAsync(customer.Id(), customer.Id(), customer.RowVersion(), customer.RowVersion());
        Assert.Equal("Customer profile to merge", await self.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var none = await owner.PostMergeAsync(customer.Id(), null, customer.RowVersion(), customer.RowVersion());
        Assert.Equal("Customer profile to merge", await none.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostMergeAsync(customer.Id(), Guid.NewGuid(), customer.RowVersion(), "")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostMergeAsync(Guid.NewGuid(), customer.Id(), "", customer.RowVersion())).StatusCode);
    }

    // A merged profile can no longer be chosen, on either side, nor edited.
    [Fact]
    public async Task A_merged_profile_is_read_only_and_cannot_take_part_in_another_merge()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();
        var other = await owner.CreateCustomerAsync();
        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);
        var view = await owner.CustomerAsync(merged.Id());

        await (await owner.MergeAsync(other.Id(), merged.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.MergeAsync(merged.Id(), other.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.GetAsync($"/api/customers/{other.Id()}/merge-preview?mergedCustomerId={merged.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var edit = await owner.PutAsJsonAsync($"/api/customers/{merged.Id()}", new { name = "X", phoneNumber = NewPhone(), rowVersion = view.RowVersion() });
        Assert.Contains("is Merged", (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09")).Text("detail"));
    }

    [Fact]
    public async Task A_merge_needs_the_row_versions_it_read()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        Assert.Equal("RowVersion", await (await owner.PostMergeAsync(kept.Id(), merged.Id(), null, merged.RowVersion())).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("RowVersion", await (await owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), null)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var stale = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        await (await owner.PostMergeAsync(kept.Id(), merged.Id(), stale, merged.RowVersion())).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await owner.PostMergeAsync(kept.Id(), merged.Id(), kept.RowVersion(), stale)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Null(await MergedIntoAsync(merged.Id()));
    }
}
