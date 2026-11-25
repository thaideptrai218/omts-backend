using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Owner decision of 25/11/2026: a Zone Manager does in Mua hàng what the Owner does, but only in the zones of its pairs: it records receipts and returns and sets prices on its receipts and reads
/// the suppliers; it never creates or edits a supplier nor pays one. The Owner and the Accountant see every receipt; a receipt with no zone is theirs alone.
/// </summary>
public class PurchaseReceiptZoneScopeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<JsonElement> ItemsAsync(HttpClient client, string route) =>
        (await client.GetFromJsonAsync<JsonElement>(route)).GetProperty("page").GetProperty("items");

    private static bool Lists(JsonElement items, Guid id) => items.EnumerateArray().Any(r => r.Id() == id);

    [Fact]
    public async Task A_manager_records_and_reads_a_receipt_of_its_zone_and_is_refused_in_another_zone_or_with_no_zone()
    {
        var world = await factory.WorldAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var (managerA, managerB) = (await factory.ZoneManagerAsync(world.Zone), await factory.ZoneManagerAsync(zoneB));
        var line = new LineBody(world.Other, 2, 5000);

        var draft = await managerA.CreateReceiptAsync(world.Body(line));
        var inB = await managerA.PostReceiptAsync(world.Body(line) with { ZoneId = zoneB.Id });
        var noZone = await managerA.PostReceiptAsync(world.Body(line) with { ZoneId = null });

        Assert.Equal(HttpStatusCode.OK, (await managerA.GetAsync($"/api/purchase-receipts/{draft.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inB.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, noZone.StatusCode);
        // The manager of zone B finds nothing of zone A, whatever it reads or writes.
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/purchase-receipts/{draft.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.EditAsync(draft, world.Body(line))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.CompleteAsync(draft.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.DeleteAsync($"/api/purchase-receipts/{draft.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/purchase-receipts/{draft.Id()}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/purchase-receipts/{draft.Id()}/copy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/purchase-receipts/{draft.Id()}/price-setup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.PostReturnAsync(draft.Id(), new ReturnBody())).StatusCode);

        var completed = await managerA.CompleteAsync(draft.Id());
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        // The manager of zone A moves the receipt to no zone or to zone B: refused, the receipt stays where it was.
        var moved = await managerA.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", world.Body(line) with { ZoneId = zoneB.Id, RowVersion = (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);
        Assert.Equal(world.Zone.Id, (await managerA.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{draft.Id()}")).GetProperty("zone").Id());
    }

    [Fact]
    public async Task The_lists_and_the_export_of_a_manager_hold_the_receipts_of_its_zones_and_the_owner_and_the_accountant_see_all_with_those_of_no_zone()
    {
        var world = await factory.WorldAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var (managerA, managerB, accountant) = (await factory.ZoneManagerAsync(world.Zone), await factory.ZoneManagerAsync(zoneB), await factory.AccountantAsync(zoneB));
        var line = new LineBody(world.Other, 1, 1000);
        var inA = await managerA.CreateReceiptAsync(world.Body(line) with { Complete = true });
        var inB = await managerB.CreateReceiptAsync(world.Body(line) with { Complete = true, ZoneId = zoneB.Id });
        var none = await world.Owner.CreateReceiptAsync(world.Body(line) with { Complete = true, ZoneId = null });
        var route = "/api/purchase-receipts?pageSize=100";

        var (a, b, owner, acc) = (await ItemsAsync(managerA, route), await ItemsAsync(managerB, route), await ItemsAsync(world.Owner, route), await ItemsAsync(accountant, route));

        Assert.True(Lists(a, inA.Id()) && !Lists(a, inB.Id()) && !Lists(a, none.Id()));
        Assert.True(Lists(b, inB.Id()) && !Lists(b, inA.Id()) && !Lists(b, none.Id()));
        Assert.All(new[] { inA, inB, none }, receipt => Assert.True(Lists(owner, receipt.Id()) && Lists(acc, receipt.Id())));
        // Asking for the zone of another manager gives nothing, not everything.
        Assert.False(Lists(await ItemsAsync(managerA, $"{route}&zoneIds={zoneB.Id}"), inB.Id()));
        Assert.Equal(HttpStatusCode.OK, (await managerA.GetAsync($"/api/purchase-receipts/export")).StatusCode);
        // The Accountant, whose pair is in zone B, still reads the receipt of zone A and the one with no zone.
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/purchase-receipts/{inA.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/purchase-receipts/{none.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerA.GetAsync($"/api/purchase-receipts/{none.Id()}")).StatusCode);
    }

    [Fact]
    public async Task A_manager_returns_goods_and_sets_prices_on_its_own_receipts_only()
    {
        var world = await factory.WorldAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var (managerA, managerB) = (await factory.ZoneManagerAsync(world.Zone), await factory.ZoneManagerAsync(zoneB));
        var receipt = await managerA.CreateReceiptAsync(world.Body(new LineBody(world.Other, 4, 2000)) with { Complete = true });
        var lineId = receipt.LineOf(world.Other);

        var refused = await managerB.PostReturnAsync(receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(lineId, 1)] });
        var returned = await managerA.PostReturnAsync(receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(lineId, 1)] });
        var priceBody = new { items = new[] { new { itemId = world.Other, costPrice = 2500m } } };
        var priceRefused = await managerB.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", priceBody);
        var priced = await managerA.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", priceBody);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, returned.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, priceRefused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);
        var returns = await ItemsAsync(managerA, "/api/purchase-returns?pageSize=100");
        Assert.Single(returns.EnumerateArray(), r => r.GetProperty("receiptCode").GetString() == receipt.GetProperty("code").GetString());
        Assert.Empty((await ItemsAsync(managerB, "/api/purchase-returns?pageSize=100")).EnumerateArray().Where(r => r.GetProperty("receiptCode").GetString() == receipt.GetProperty("code").GetString()));
        var returnId = (await ItemsAsync(managerA, "/api/purchase-returns?pageSize=100")).EnumerateArray().First(r => r.GetProperty("receiptCode").GetString() == receipt.GetProperty("code").GetString()).Id();
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/purchase-returns/{returnId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerA.GetAsync($"/api/purchase-returns/{returnId}")).StatusCode);
    }

    [Fact]
    public async Task A_manager_reads_the_suppliers_but_creates_edits_and_pays_none()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var accountant = await factory.AccountantAsync();

        var list = await manager.GetAsync("/api/suppliers");
        var one = await manager.GetAsync($"/api/suppliers/{world.SupplierId}");
        var create = await manager.PostAsJsonAsync("/api/suppliers", new { name = $"Supplier {Guid.NewGuid():N}", supplierType = "Seed", phoneNumber = "0912345679" });
        var edit = await manager.PutAsJsonAsync($"/api/suppliers/{world.SupplierId}", new { name = "Renamed", supplierType = "Seed" });
        var pay = await manager.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1000, ConfirmAdvance = true });
        var debts = await manager.GetAsync("/api/supplier-debts");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pay.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, debts.StatusCode);
        // The payment stays the Accountant's.
        Assert.Equal(HttpStatusCode.Created, (await accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1000, ConfirmAdvance = true })).StatusCode);
    }
}
