using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: exactly which fields each role reads, prices and phone numbers above all.</summary>
public class SalesCatalogueQaReadFieldsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly string StaffPassword = NewPassword();

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    // What each read shows: exactly these fields, a trader never a price, also by id and on a Discontinued product.
    [Fact]
    public async Task Each_role_reads_exactly_its_fields_and_a_trader_never_a_price()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var tag = NewCode();
        var inUse = await owner.CreateProductAsync($"{tag} a", 42_000);
        var off = await owner.CreateProductAsync($"{tag} b", 43_000);
        await (await owner.PostAsync($"/api/products/{off.Id()}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        var manager = await factory.ZoneManagerAsync(await factory.AddZoneAsync(NewCode()));
        var traderAccount = await factory.AddAccountAsync(NewCode(), StaffPassword, SubjectType.Trader);
        var trader = await factory.LoginAsync(traderAccount.Username, StaffPassword);
        string[] productFields = ["id", "name", "rowVersion", "status", "statusId", "unitPrice"];

        foreach (var url in new[] { $"/api/products?search={tag}", $"/api/products?search={tag}&statusId={StatusIds.Usage.Discontinued}" })
        {
            var rows = await (await trader.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
            var row = Assert.Single(rows.EnumerateArray());
            Assert.Equal(inUse.Id(), row.Id());
            Assert.Equal(productFields, Keys(row));
            Assert.Equal(JsonValueKind.Null, row.GetProperty("unitPrice").ValueKind);
        }

        var discontinued = await (await trader.GetAsync($"/api/products/{off.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, discontinued.GetProperty("unitPrice").ValueKind);
        Assert.DoesNotContain("43000", discontinued.GetRawText());

        var forManager = await (await manager.GetAsync($"/api/products/{inUse.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(productFields, Keys(forManager));
        Assert.Equal(42_000, forManager.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task The_customer_reads_hold_the_screen_fields_and_nothing_of_the_account_but_its_name()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var (_, linked) = await factory.LinkedProfileAsync(name: $"Linked {NewCode()}");
        var list = await (await owner.GetAsync($"/api/customers?search={Uri.EscapeDataString(linked.Name)}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(
            [
                "address", "birthDate", "code", "companyName", "createdAt", "createdBy", "currentDebt", "customerType", "email", "facebook", "gender", "hasTraderAccount", "id",
                "lastTransactionAt", "name", "netSales", "note", "phoneNumber", "phoneNumber2", "photoUrl", "province", "rowVersion", "status", "statusId", "taxCode", "totalSales", "ward",
            ],
            Keys(Assert.Single(list.GetProperty("items").EnumerateArray())));
        var view = await owner.CustomerAsync(linked.Id);
        Assert.Equal(
            [
                "address", "bankAccountNumber", "bankBin", "bankName", "birthDate", "code", "companyName", "createdAt", "createdBy", "currentDebt", "customerType", "email", "facebook",
                "gender", "id", "identityNumber", "isUsed", "mergedIntoCustomerId", "name", "note", "phoneLocked", "phoneNumber", "phoneNumber2", "photoUrl", "province", "rowVersion",
                "status", "statusId", "taxCode", "traderAccount", "ward",
            ],
            Keys(view));
        Assert.Equal(["accountId", "displayName", "username"], Keys(view.GetProperty("traderAccount")));
    }
}
