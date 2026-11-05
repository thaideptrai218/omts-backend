using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-04.3, BR-SALE-04: the merge refusals and confirmations, re-pointing, audit.</summary>
public class CustomerMergeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<HttpClient> ManagerAsync() => await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));

    private Task<Guid?> MergedIntoAsync(Guid id) =>
        factory.QueryAsync(db => db.Customers.Where(c => c.Id == id).Select(c => c.MergedIntoCustomerId).SingleAsync());

    // MSG-SALE-04: refused when false, with what the Zone Manager is asked; nothing changes.
    [Fact]
    public async Task A_zone_manager_without_the_owner_agreement_gets_MSG_SALE_04_and_nothing_changes()
    {
        var manager = await ManagerAsync();
        var kept = await manager.CreateCustomerAsync(name: "Chị Hoa", phone: NewPhone());
        var merged = await manager.CreateCustomerAsync(name: "Hoa cũ", phone: NewPhone());

        var response = await manager.MergeAsync(kept.Id(), merged.Id());

        var body = await response.ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-04");
        Assert.Equal(
            $"Has the Cooperative Owner agreed to merge Hoa cũ ({merged.Text("phoneNumber")}, debt 0 VND) into Chị Hoa ({kept.Text("phoneNumber")}, debt 0 VND)? The merge cannot be undone.",
            body.Text("detail"));
        Assert.Equal(merged.Id(), body.GetProperty("merged").Id());
        Assert.Equal(kept.Id(), body.GetProperty("kept").Id());
        Assert.Null(await MergedIntoAsync(merged.Id()));
        Assert.Empty(await factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "Customer.Merged" && e.RecordId == merged.Id()).ToListAsync()));
    }

    [Fact]
    public async Task A_zone_manager_with_the_owner_agreement_merges_and_the_merged_profile_is_no_longer_offered()
    {
        var manager = await ManagerAsync();
        var tag = NewCode();
        var kept = await manager.CreateCustomerAsync(name: $"Kept {tag}");
        var merged = await manager.CreateCustomerAsync(name: $"Merged {tag}");

        var body = await (await manager.MergeAsync(kept.Id(), merged.Id(), ownerApproved: true)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((kept.Id(), kept.Text("name"), kept.Text("phoneNumber")), (body.Id(), body.Text("name"), body.Text("phoneNumber")));
        Assert.Equal(kept.Id(), await MergedIntoAsync(merged.Id()));
        var list = await (await manager.GetAsync($"/api/customers?search={tag}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(kept.Id(), Assert.Single(list.GetProperty("items").EnumerateArray()).Id());
        var view = await manager.CustomerAsync(merged.Id());
        Assert.Equal(kept.Id(), view.GetProperty("mergedIntoCustomerId").GetGuid());

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Customer.Merged" && e.RecordId == merged.Id()));
        Assert.NotNull(audit.ActorAccountId);
        var after = JsonDocument.Parse(audit.After!).RootElement;
        Assert.Equal(kept.Id(), after.GetProperty("MergedIntoCustomerId").GetGuid());
        Assert.True(after.GetProperty("OwnerApproved").GetBoolean());
    }

    // BR-SALE-04: when the Cooperative Owner merges, the system asks only for confirmation.
    [Fact]
    public async Task The_owner_merges_without_the_flag()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var merged = await owner.CreateCustomerAsync();

        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(kept.Id(), await MergedIntoAsync(merged.Id()));
    }

    // POST-2: the kept profile keeps its own name, phone number and trader account.
    [Fact]
    public async Task A_profile_with_a_trader_account_can_be_the_kept_one()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (trader, kept) = await factory.LinkedProfileAsync(name: "Linked");
        var merged = await owner.CreateCustomerAsync(name: "Offline");

        var body = await (await owner.MergeAsync(kept.Id, merged.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Linked", trader.PhoneNumber, trader.Id), (body.Text("name"), body.Text("phoneNumber"), body.GetProperty("traderAccount").GetProperty("accountId").GetGuid()));
    }

    // The references of today: a profile merged earlier into the one being merged follows it, so no chain is left.
    [Fact]
    public async Task A_profile_merged_earlier_follows_to_the_kept_profile_and_the_audit_lists_it()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var middle = await owner.CreateCustomerAsync();
        var earlier = await owner.CreateCustomerAsync();
        await (await owner.MergeAsync(middle.Id(), earlier.Id())).ReadAsync(HttpStatusCode.OK);

        var preview = await (await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={middle.Id()}")).ReadAsync(HttpStatusCode.OK);
        var counted = Assert.Single(preview.GetProperty("countedWithKept").EnumerateArray());
        Assert.Equal(("Merged customer profiles", 1), (counted.Text("name"), counted.GetProperty("count").GetInt32()));

        await (await owner.MergeAsync(kept.Id(), middle.Id())).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(kept.Id(), await MergedIntoAsync(earlier.Id()));
        Assert.Equal(kept.Id(), await MergedIntoAsync(middle.Id()));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Customer.Merged" && e.RecordId == middle.Id()));
        var moved = Assert.Single(JsonDocument.Parse(audit.After!).RootElement.GetProperty("Moved").EnumerateArray());
        Assert.Equal(earlier.Id(), Assert.Single(moved.GetProperty("RowIds").EnumerateArray()).GetGuid());
    }

    // The one place that lists the references: a new table with a foreign key to Customer fails here until it adds its line.
    [Fact]
    public async Task Every_foreign_key_to_a_customer_profile_is_listed_for_the_merge()
    {
        await factory.EnsureCooperativeAsync();
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Model;
        var columns = scope.ServiceProvider.GetRequiredService<ICustomerReferences>().Columns;

        var foreignKeys = model.FindEntityType(typeof(Customer))!.GetReferencingForeignKeys()
            .Select(k => (k.DeclaringEntityType.ClrType, Assert.Single(k.Properties).Name))
            .OrderBy(k => k.ToString());

        Assert.Equal(foreignKeys, columns.Select(c => (c.RowType, c.Column)).OrderBy(k => k.ToString()));
    }

    [Fact]
    public async Task The_preview_asks_a_zone_manager_for_the_owner_agreement_and_the_owner_only_to_confirm_and_refuses_like_the_merge()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var manager = await ManagerAsync();
        var kept = await owner.CreateCustomerAsync(name: "Kept");
        var merged = await owner.CreateCustomerAsync(name: "Merged");
        var url = $"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={merged.Id()}";

        var forManager = await (await manager.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        Assert.True(forManager.GetProperty("requiresOwnerApproval").GetBoolean());
        Assert.StartsWith("Has the Cooperative Owner agreed to merge Merged", forManager.Text("confirmation"));
        Assert.Equal(merged.RowVersion(), forManager.GetProperty("merged").RowVersion());
        Assert.Equal(0, forManager.GetProperty("kept").GetProperty("currentDebt").GetDecimal());

        var forOwner = await (await owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        Assert.False(forOwner.GetProperty("requiresOwnerApproval").GetBoolean());
        Assert.Equal(JsonValueKind.Null, forOwner.GetProperty("confirmation").ValueKind);

        var (_, linked) = await factory.LinkedProfileAsync();
        await (await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={linked.Id}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-SALE-05");
        await (await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/customers/{kept.Id()}/merge-preview?mergedCustomerId={Guid.NewGuid()}")).StatusCode);
    }

    // A merged number is free again: only the kept profile counts (BR-SALE-04).
    [Fact]
    public async Task The_number_of_a_merged_profile_can_be_used_again()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var kept = await owner.CreateCustomerAsync();
        var phone = NewPhone();
        var merged = await owner.CreateCustomerAsync(phone: phone);
        await (await owner.MergeAsync(kept.Id(), merged.Id())).ReadAsync(HttpStatusCode.OK);

        var again = await owner.CreateCustomerAsync(phone: phone);

        Assert.NotEqual(merged.Id(), again.Id());
    }
}
