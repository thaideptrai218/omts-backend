using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The product catalogue: codes (typed or SP000001), the base unit kg and the extra units with a factor, the price history, the groups, the paged list with the kg on hand, who may write and who may read,
/// and the removal of an unused product against the hiding of a used one.
/// </summary>
public class ProductCatalogueTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Task<HttpResponseMessage> PostProductAsync(HttpClient client, object body) => client.PostAsJsonAsync("/api/products", body);

    private static IEnumerable<JsonElement> Units(JsonElement product) => product.GetProperty("units").EnumerateArray();

    [Fact]
    public async Task A_new_product_gets_the_next_SP_code_and_kg_as_its_only_unit()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var product = await owner.CreateProductAsync();

        Assert.Matches("^SP[0-9]{6,}$", product.Text("code"));
        Assert.Equal("kg", product.Text("unit"));
        var unit = Assert.Single(Units(product));
        Assert.Equal((true, 1m, $"{product.Text("code")}-K"), (unit.GetProperty("isBase").GetBoolean(), unit.GetProperty("factor").GetDecimal(), unit.Text("code")));
        Assert.Equal(0m, product.GetProperty("onHandKg").GetDecimal());
    }

    [Fact]
    public async Task A_typed_code_is_kept_and_is_unique_regardless_of_case_among_product_and_unit_codes()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var code = $"Hau{NewCode()}"[..14];
        var first = await (await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, code })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(code, first.GetProperty("product").Text("code"));

        var sameCode = await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, code = code.ToUpperInvariant() });
        Assert.Equal("Code", await sameCode.FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));

        var asUnitCode = await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, code = $"{code}-K" });
        Assert.Equal("Code", await asUnitCode.FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));
    }

    [Theory]
    [InlineData("not valid!")]
    [InlineData("this-code-is-much-longer-than-thirty-characters")]
    public async Task A_typed_code_with_other_characters_or_over_30_characters_is_refused(string code)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, code });

        Assert.Equal("Code", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Extra_units_carry_a_factor_in_kg_and_a_code_made_of_the_product_code_and_the_unit_letter()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var body = await (await PostProductAsync(owner, new
        {
            name = $"Hàu {NewCode()}", unitPrice = 60_000,
            units = new object[] { new { unitName = "Lồng", factor = 25 }, new { unitName = "Thùng", factor = 10.5, code = $"TH{NewCode()}"[..10] } },
        })).ReadAsync(HttpStatusCode.Created);

        var product = body.GetProperty("product");
        var extras = Units(product).Where(u => !u.GetProperty("isBase").GetBoolean()).OrderBy(u => u.GetProperty("factor").GetDecimal()).ToList();
        Assert.Equal([10.5m, 25m], extras.Select(u => u.GetProperty("factor").GetDecimal()));
        Assert.Equal($"{product.Text("code")}-L", extras[1].Text("code"));
        Assert.Equal(["Thùng", "Lồng"], extras.Select(u => u.Text("name")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task A_unit_factor_must_be_above_0_and_at_most_a_million(double factor)
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, units = new object[] { new { unitName = "Lồng", factor } } });

        Assert.Equal("Factor", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Kg_cannot_be_added_as_an_extra_unit()
    {
        var owner = await factory.PurchasingOwnerAsync();

        var response = await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, units = new object[] { new { unitName = "KG", factor = 2 } } });

        Assert.Equal("Units", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_price_history_starts_with_the_first_price_and_a_changed_price_adds_one_row_while_an_unchanged_one_adds_none()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);

        var first = await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 62_500, rowVersion = product.RowVersion() })).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 62_500, rowVersion = first.GetProperty("product").RowVersion() })).ReadAsync(HttpStatusCode.OK);

        var history = (await owner.GetFromJsonAsync<JsonElement>($"/api/products/{product.Id()}/price-history")).EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal((60_000m, 62_500m), (history[1].GetProperty("newPrice").GetDecimal(), history[0].GetProperty("newPrice").GetDecimal()));
        Assert.Equal(60_000m, history[0].GetProperty("oldPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("oldPrice").ValueKind);
    }

    [Fact]
    public async Task A_product_group_has_two_levels_and_a_group_with_products_is_not_deleted()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var parent = await (await owner.PostAsJsonAsync("/api/product-categories", new { name = $"Hàu {NewCode()}" })).ReadAsync(HttpStatusCode.Created);
        var child = await (await owner.PostAsJsonAsync("/api/product-categories", new { name = $"Cỡ {NewCode()}", parentId = parent.Id() })).ReadAsync(HttpStatusCode.Created);
        var grandchild = await owner.PostAsJsonAsync("/api/product-categories", new { name = $"Nhỏ {NewCode()}", parentId = child.Id() });
        Assert.Equal("Parent", await grandchild.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var product = await (await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, categoryId = child.Id() })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(child.Text("name"), product.GetProperty("product").Text("categoryName"));
        await (await owner.DeleteAsync($"/api/product-categories/{child.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        var parentView = (await owner.GetFromJsonAsync<JsonElement>("/api/product-categories")).EnumerateArray().Single(g => g.Id() == parent.Id());
        Assert.Equal(1, parentView.GetProperty("productCount").GetInt32());
    }

    [Fact]
    public async Task The_paged_list_shows_the_kg_on_hand_filters_by_status_and_search_and_sorts_by_code_newest_first()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var marker = NewCode();
        var older = await owner.CreateProductAsync($"Hàu {marker} a");
        var newer = await owner.CreateProductAsync($"Hàu {marker} b");
        await owner.PostAsync($"/api/products/{older.Id()}/discontinue", null);

        var inUse = await owner.GetFromJsonAsync<JsonElement>($"/api/products/search?search={marker}");
        var all = await owner.GetFromJsonAsync<JsonElement>($"/api/products/search?search={marker}&status=All");

        Assert.Equal([newer.Id()], inUse.GetProperty("items").EnumerateArray().Select(i => i.Id()));
        Assert.Equal([newer.Id(), older.Id()], all.GetProperty("items").EnumerateArray().Select(i => i.Id()));
        Assert.Equal(0m, all.GetProperty("summary").GetProperty("totalOnHandKg").GetDecimal());
        Assert.Equal(0m, all.GetProperty("items")[0].GetProperty("onHandKg").GetDecimal());
    }

    [Fact]
    public async Task Only_the_owner_writes_the_catalogue_while_a_zone_manager_and_an_accountant_view_it()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var (zone, _) = await factory.ZoneWithRaftAsync();
        var manager = await factory.ZoneManagerAsync(zone);
        var accountant = await factory.AccountantAsync();

        foreach (var staff in new[] { manager, accountant })
        {
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/products/search")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/products/{product.Id()}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/products/{product.Id()}/movements")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/products/{product.Id()}/price-history")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PostProductAsync(staff, new { name = $"Hàu {NewCode()}", unitPrice = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"/api/products/{product.Id()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/product-categories", new { name = $"G {NewCode()}" })).StatusCode);
        }
    }

    [Fact]
    public async Task A_product_nothing_refers_to_is_removed_with_its_units_and_price_history()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await (await PostProductAsync(owner, new { name = $"Hàu {NewCode()}", unitPrice = 60_000, units = new object[] { new { unitName = "Lồng", factor = 25 } } })).ReadAsync(HttpStatusCode.Created);
        var id = product.GetProperty("product").Id();

        var deleted = await (await owner.DeleteAsync($"/api/products/{id}")).ReadAsync(HttpStatusCode.OK);

        Assert.True(deleted.GetProperty("removed").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/products/{id}")).StatusCode);
        Assert.Equal((0, 0), (await factory.QueryAsync(db => db.ProductUnits.CountAsync(u => u.ProductId == id)), await factory.QueryAsync(db => db.ProductPriceChanges.CountAsync(c => c.ProductId == id))));
    }

    [Fact]
    public async Task A_product_with_history_is_hidden_when_deleted_it_leaves_every_list_but_keeps_its_code_and_name()
    {
        var setup = await factory.SaleSetupAsync();
        var client = setup.Owner.Client;
        var lot = await client.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(3).WithProducts((setup.ProductId, 2, 50m)));
        var product = await client.GetFromJsonAsync<JsonElement>($"/api/products/{setup.ProductId}");

        var deleted = await (await client.DeleteAsync($"/api/products/{setup.ProductId}")).ReadAsync(HttpStatusCode.OK);

        Assert.False(deleted.GetProperty("removed").GetBoolean());
        Assert.DoesNotContain(setup.ProductId, (await client.GetFromJsonAsync<JsonElement>("/api/products/search?status=All&pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Id()));
        Assert.DoesNotContain(setup.ProductId, (await client.GetFromJsonAsync<JsonElement>("/api/products")).EnumerateArray().Select(i => i.Id()));
        var again = await PostProductAsync(client, new { name = product.Text("name"), unitPrice = 1 });
        Assert.Equal("Name", await again.FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));
        Assert.Equal(StatusIds.Usage.Discontinued, (await client.GetFromJsonAsync<JsonElement>($"/api/products/{setup.ProductId}")).GetProperty("statusId").GetInt32());
        Assert.NotEqual(Guid.Empty, lot.Id());
    }

    [Fact]
    public async Task An_extra_unit_starts_at_factor_times_the_kg_price_then_has_its_own_price_and_history_per_unit()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var body = await (await PostProductAsync(owner, new
        {
            name = $"Hàu {NewCode()}", unitPrice = 60_000, units = new object[] { new { unitName = "Lồng", factor = 25 }, new { unitName = "Thùng", factor = 10, salePrice = 700_000 } },
        })).ReadAsync(HttpStatusCode.Created);
        var product = body.GetProperty("product");
        var units = Units(product).ToDictionary(u => u.Text("name")!, u => u);
        Assert.Equal((60_000m, 1_500_000m, 700_000m), (units["kg"].GetProperty("salePrice").GetDecimal(), units["Lồng"].GetProperty("salePrice").GetDecimal(), units["Thùng"].GetProperty("salePrice").GetDecimal()));

        // A new kg price moves the base unit only; the Lồng price is changed on its own.
        var extras = Units(product).Where(u => !u.GetProperty("isBase").GetBoolean())
            .Select(u => new { id = u.Id(), unitId = u.GetProperty("unitId").GetGuid(), factor = u.GetProperty("factor").GetDecimal(), code = u.Text("code"), salePrice = u.Text("name") == "Lồng" ? 1_600_000m : u.GetProperty("salePrice").GetDecimal() }).ToList();
        var updated = await (await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = product.Text("name"), unitPrice = 65_000, rowVersion = product.RowVersion(), units = extras })).ReadAsync(HttpStatusCode.OK);
        var after = Units(updated.GetProperty("product")).ToDictionary(u => u.Text("name")!, u => u.GetProperty("salePrice").GetDecimal());
        Assert.Equal((65_000m, 1_600_000m, 700_000m), (after["kg"], after["Lồng"], after["Thùng"]));

        var history = (await owner.GetFromJsonAsync<JsonElement>($"/api/products/{product.Id()}/price-history")).EnumerateArray().ToList();
        Assert.Equal(5, history.Count);
        var cage = units["Lồng"].GetProperty("unitId").GetGuid();
        var cageHistory = (await owner.GetFromJsonAsync<JsonElement>($"/api/products/{product.Id()}/price-history?unitId={cage}")).EnumerateArray().ToList();
        Assert.Equal([1_600_000m, 1_500_000m], cageHistory.Select(h => h.GetProperty("newPrice").GetDecimal()));
        Assert.All(cageHistory, h => Assert.Equal("Lồng", h.Text("unitName")));
    }
}
