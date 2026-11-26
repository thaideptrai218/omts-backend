using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Owner decision of 26/11/2026 (shared warehouse, no zone limit): a Zone Manager does in Mua hàng what the Owner does in every zone, receipts, returns and prices included; it never
/// creates or edits a supplier nor pays one. The Owner and the Accountant see everything.
/// </summary>
public class ZoneManagerPurchasingRightsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<JsonElement> ItemsAsync(HttpClient client, string route) =>
        (await client.GetFromJsonAsync<JsonElement>(route)).GetProperty("page").GetProperty("items");

    private static bool Lists(JsonElement items, Guid id) => items.EnumerateArray().Any(r => r.Id() == id);

    [Fact]
    public async Task A_manager_of_one_zone_reads_lists_exports_and_prices_the_receipts_of_another_zone_and_the_accountant_and_owner_see_all()
    {
        var world = await factory.WorldAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var (managerA, managerB, accountant) = (await factory.ZoneManagerAsync(world.Zone), await factory.ZoneManagerAsync(zoneB), await factory.AccountantAsync(zoneB));
        var line = new LineBody(world.Other, 4, 2000);
        var receipt = await managerA.CreateReceiptAsync(world.Body(line) with { Complete = true });
        var route = "/api/purchase-receipts?pageSize=100";

        foreach (var reader in new[] { managerA, managerB, accountant, world.Owner })
        {
            Assert.True(Lists(await ItemsAsync(reader, route), receipt.Id()));
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/purchase-receipts/{receipt.Id()}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await managerB.GetAsync("/api/purchase-receipts/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await managerB.GetAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup")).StatusCode);
        var priced = await managerB.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", new { items = new[] { new { itemId = world.Other, costPrice = 2500m } } });
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup")).StatusCode);

        // The manager of zone B records a receipt too, and the one of zone A reads it.
        var made = await managerB.CreateReceiptAsync(world.Body(line) with { Complete = true });
        Assert.Equal(HttpStatusCode.OK, (await managerA.GetAsync($"/api/purchase-receipts/{made.Id()}")).StatusCode);
    }

    [Fact]
    public async Task A_manager_returns_goods_of_a_receipt_of_another_zone()
    {
        var world = await factory.WorldAsync();
        var (zoneB, _) = await factory.ZoneWithRaftAsync();
        var (managerA, managerB) = (await factory.ZoneManagerAsync(world.Zone), await factory.ZoneManagerAsync(zoneB));
        var receipt = await managerA.CreateReceiptAsync(world.Body(new LineBody(world.Other, 4, 2000)) with { Complete = true });
        var lineId = receipt.LineOf(world.Other);

        var returned = await managerB.PostReturnAsync(receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(lineId, 1)] });

        Assert.Equal(HttpStatusCode.Created, returned.StatusCode);
        var code = receipt.GetProperty("code").GetString();
        var returnId = (await ItemsAsync(managerA, "/api/purchase-returns?pageSize=100")).EnumerateArray().First(r => r.GetProperty("receiptCode").GetString() == code).Id();
        Assert.Equal(HttpStatusCode.OK, (await managerB.GetAsync($"/api/purchase-returns/{returnId}")).StatusCode);
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
