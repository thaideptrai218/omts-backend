using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-01 rules and error bullets: validation, duplicates, discontinue/reactivate, detail, grants and audit.</summary>
public class SupplierRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(null, "Seed", "0912345678", "Name")]
    [InlineData("Supplier", null, "0912345678", "Supplier type")]
    [InlineData("Supplier", "Seed", null, "Phone number")]
    [InlineData("Supplier", "Seed", "12345", "Phone number")]
    [InlineData("Supplier", "Seed", "09123abc45", "Phone number")]
    public async Task Missing_name_type_or_phone_and_a_bad_phone_return_MSG01_naming_the_field(string? name, string? type, string? phone, string field)
    {
        var owner = await factory.PurchasingOwnerAsync();
        var before = await factory.QueryAsync(db => db.Suppliers.CountAsync());

        var response = await owner.PostAsJsonAsync("/api/suppliers", new { name, supplierType = type, phoneNumber = phone });

        Assert.Equal(field, await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(before, await factory.QueryAsync(db => db.Suppliers.CountAsync()));
    }

    [Fact]
    public async Task Too_long_name_or_contacts_return_MSG01()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var longName = await owner.PostAsJsonAsync("/api/suppliers", new { name = new string('a', 201), supplierType = "Seed", phoneNumber = NewPhone() });
        Assert.Equal("Name", await longName.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var longContacts = await owner.PostAsJsonAsync("/api/suppliers", new { name = "S", supplierType = "Seed", phoneNumber = NewPhone(), otherContacts = new string('a', 501) });
        Assert.Equal("Other contacts", await longContacts.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_phone_written_another_way_is_the_same_supplier_on_create_and_on_edit()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phone = NewPhone();
        var first = await owner.CreateSupplierAsync(phone: phone);
        var formatted = $"+84 {phone[1..4]} {phone[4..7]} {phone[7..]}";

        var duplicate = await owner.PostAsJsonAsync("/api/suppliers", new { name = "Copy", supplierType = "Material", phoneNumber = formatted });
        Assert.Equal(first, (await duplicate.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        var second = await owner.CreateSupplierAsync();
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{second}");
        var edit = await owner.PutAsJsonAsync($"/api/suppliers/{second}", new
        {
            name = "Second", supplierType = "Seed", phoneNumber = formatted, rowVersion = view.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(first, (await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());
        Assert.Equal(phone, await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == first).Select(s => s.PhoneNumber).SingleAsync()));
    }

    // Done when: a Zone Manager with a supplier grant in one zone can create, edit and discontinue a supplier.
    [Fact]
    public async Task A_zone_manager_with_a_grant_in_one_zone_creates_edits_discontinues_and_reactivates_with_audit()
    {
        var (zone, _) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(zone);

        var id = await manager.CreateSupplierAsync(type: "Material");
        var view = await manager.GetFromJsonAsync<JsonElement>($"/api/suppliers/{id}");
        var phone = view.GetProperty("phoneNumber").GetString();
        var edited = await (await manager.PutAsJsonAsync($"/api/suppliers/{id}", new
        {
            name = "Phao xốp Hạ Long", supplierType = "Material", phoneNumber = phone, otherContacts = "Zalo 0900000000",
            rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Phao xốp Hạ Long", edited.GetProperty("name").GetString());

        var discontinued = await (await manager.PostAsync($"/api/suppliers/{id}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Discontinued", discontinued.GetProperty("status").GetString());
        await (await manager.PostAsync($"/api/suppliers/{id}/discontinue", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var reactivated = await (await manager.PostAsync($"/api/suppliers/{id}/reactivate", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("In Use", reactivated.GetProperty("status").GetString());
        await (await manager.PostAsync($"/api/suppliers/{id}/reactivate", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal(
            ["Supplier.Created", "Supplier.Updated", "Supplier.Discontinued", "Supplier.Reactivated"],
            await factory.AuditActionsAsync(id));
        var updated = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == id && e.Action == "Supplier.Updated"));
        Assert.Contains("Phao", updated.After);
        Assert.NotNull(updated.Before);
    }

    [Fact]
    public async Task Discontinue_is_never_blocked_and_keeps_receipts_but_blocks_new_completions()
    {
        var world = await factory.WorldAsync();
        var completed = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 100_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));

        await (await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var kept = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{completed.Id()}");
        Assert.Equal("Completed", kept.GetProperty("status").GetString());
        var complete = await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null);
        Assert.Equal("Supplier", await complete.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var direct = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { Complete = true });
        Assert.Equal("Supplier", await direct.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(0, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == draft.Id())));

        await (await world.Owner.PostAsync($"/api/suppliers/{world.SupplierId}/reactivate", null)).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/complete", null)).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search_by_name_or_phone_type_and_status_is_paged_and_pickers_get_in_use_only()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var phone = NewPhone();
        var seedA = await owner.CreateSupplierAsync("Seed", phone, $"Giống {tag} A");
        var seedB = await owner.CreateSupplierAsync("Seed", name: $"Giống {tag} B");
        var material = await owner.CreateSupplierAsync("Material", name: $"Vật tư {tag}");
        await (await owner.PostAsync($"/api/suppliers/{seedB}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        async Task<List<Guid>> Ids(string query) =>
            (await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers?{query}")).GetProperty("items").EnumerateArray().Select(s => s.Id()).ToList();

        Assert.Equal(new[] { seedA, seedB, material }.Order(), (await Ids($"search={tag}")).Order());
        Assert.Equal([seedA], await Ids($"search={phone[3..]}"));
        Assert.Equal([material], await Ids($"search={tag}&type=Material"));
        var inUse = await Ids($"search={tag}&type=Seed&statusId={Domain.Constants.StatusIds.Usage.InUse}");
        Assert.Equal([seedA], inUse);

        var firstPage = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers?search={tag}&page=1&pageSize=2");
        Assert.Equal(3, firstPage.GetProperty("total").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        var secondPage = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers?search={tag}&page=2&pageSize=2");
        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Detail_returns_the_receipt_history_of_visible_zones_and_the_current_debt()
    {
        var world = await factory.WorldAsync();
        var first = await world.CompletedAsync(0, 200_000, new LineBody(world.Other, 10, 100_000));
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 50_000)));
        var (otherZone, _) = await factory.ZoneWithRaftAsync();
        var elsewhere = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 3, 100_000)) with { ZoneId = otherZone.Id, Complete = true });

        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}");
        Assert.Equal(800_000 + 300_000, view.Money("debt"));
        var history = view.GetProperty("receipts").EnumerateArray().Select(r => r.Id()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { first.Id(), draft.Id(), elsewhere.Id() }, history);

        // A Zone Manager of the first zone sees that zone's receipts only.
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var managerView = await manager.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}");
        Assert.DoesNotContain(managerView.GetProperty("receipts").EnumerateArray(), r => r.Id() == elsewhere.Id());
        Assert.Contains(managerView.GetProperty("receipts").EnumerateArray(), r => r.Id() == first.Id());
    }

    [Fact]
    public async Task Callers_without_a_suppliers_grant_get_MSG02_and_a_view_grant_cannot_write()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var supplierId = await owner.CreateSupplierAsync();
        var (zone, _) = await factory.ZoneWithRaftAsync();

        var noGrant = await factory.PurchasingStaffAsync(zone, Perm.Full(PermissionFunctions.PurchaseReceipts));
        await (await noGrant.GetAsync("/api/suppliers")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await noGrant.GetAsync($"/api/suppliers/{supplierId}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await noGrant.PostAsJsonAsync("/api/suppliers", new { name = "X", supplierType = "Seed", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");

        var viewer = await factory.PurchasingStaffAsync(zone, new Perm(PermissionFunctions.Suppliers));
        await (await viewer.GetAsync($"/api/suppliers/{supplierId}")).ReadAsync(HttpStatusCode.OK);
        await (await viewer.PostAsJsonAsync("/api/suppliers", new { name = "X", supplierType = "Seed", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.PutAsJsonAsync($"/api/suppliers/{supplierId}", new { name = "X", supplierType = "Seed", phoneNumber = NewPhone(), rowVersion = "" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.PostAsync($"/api/suppliers/{supplierId}/discontinue", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(Domain.Constants.StatusIds.Usage.InUse, (await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == supplierId))).StatusId);

        // Supplier debt is INP-06: All zones SUPPLIER_PAYMENTS, so a Zone Manager cannot read it.
        var manager = await factory.ZoneManagerAsync(zone);
        await (await manager.GetAsync($"/api/suppliers/{supplierId}/debt")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.GetAsync("/api/suppliers/debts")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task Edit_of_a_supplier_someone_saved_meanwhile_returns_MSG06_and_keeps_their_version()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateSupplierAsync();
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{id}");
        // InMemory does not generate rowversions: store one as SQL Server would after another user's save.
        await factory.UpdateAsync<Supplier>(id, s => { s.Name = "Saved by someone else"; s.RowVersion = [0, 0, 0, 0, 0, 0, 9, 1]; });

        var stale = await owner.PutAsJsonAsync($"/api/suppliers/{id}", new
        {
            name = "My change", supplierType = "Seed", phoneNumber = view.GetProperty("phoneNumber").GetString(), rowVersion = "AAAAAAAACQA=",
        });

        await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal("Saved by someone else", (await factory.QueryAsync(db => db.Suppliers.SingleAsync(s => s.Id == id))).Name);
    }
}
