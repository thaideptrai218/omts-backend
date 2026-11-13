using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The input item catalogue: units of measure, item groups, inventory settings, the units of an item and the quantity on hand and cost that receipts, returns and the opening quantity
/// write on the movement ledger. Each test starts its own host, so the catalogue is empty.
/// </summary>
public sealed class InputItemCatalogueTests
{
    private const string Items = "/api/farming-input-items";
    private const string Units = "/api/input-units";
    private const string Groups = "/api/input-item-groups";
    private const string Settings = "/api/input-inventory-settings";

    [Fact]
    public async Task The_unit_catalogue_has_unique_names_renames_the_items_and_keeps_used_units()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var box = await (await owner.PostAsJsonAsync(Units, new { name = "thùng" })).ReadAsync(HttpStatusCode.Created);
        var duplicate = await owner.PostAsJsonAsync(Units, new { name = "THÙNG" });
        var problem = await duplicate.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((box.Id(), "Name"), (problem.GetProperty("existingId").GetGuid(), problem.GetProperty("field").GetString()));
        await (await owner.PostAsJsonAsync(Units, new { name = "  " })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        // A unit nobody uses is deleted.
        var spare = await (await owner.PostAsJsonAsync(Units, new { name = "bó" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Units}/{spare.Id()}")).StatusCode);

        // An item counting in a unit keeps it, and a rename follows to the base unit shown on the item.
        var item = await (await owner.PostAsJsonAsync(Items, new { name = "Dây thừng", group = "Material", unit = "cuộn" })).ReadAsync(HttpStatusCode.Created);
        var roll = (await owner.GetFromJsonAsync<JsonElement>(Units)).EnumerateArray().Single(u => u.GetProperty("name").GetString() == "cuộn");
        Assert.Equal(1, roll.GetProperty("itemCount").GetInt32());
        await (await owner.DeleteAsync($"{Units}/{roll.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        var renamed = await (await owner.PutAsJsonAsync($"{Units}/{roll.Id()}", new { name = "cuộn 200 m", rowVersion = roll.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("cuộn 200 m", renamed.GetProperty("name").GetString());
        Assert.Equal("cuộn 200 m", (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item.Id()}")).GetProperty("unit").GetString());
    }

    [Fact]
    public async Task Staff_read_the_catalogue_but_only_the_Owner_writes_it()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var (zone, _) = await api.ZoneWithRaftAsync();
        var manager = await api.ZoneManagerAsync(zone);
        var accountant = await api.AccountantAsync();

        foreach (var reader in new[] { manager, accountant })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Units)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Groups)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Settings)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Items}/search")).StatusCode);
            await (await reader.PostAsJsonAsync(Units, new { name = "kg" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PostAsJsonAsync(Groups, new { name = "Lưới" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
            await (await reader.PutAsJsonAsync(Settings, new { costMethod = "Average", allowNegativeQuantity = false, rowVersion = "" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{Items}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"{Items}/export")).StatusCode);
    }

    [Fact]
    public async Task Item_groups_are_ordered_counted_and_deleted_only_when_empty()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var nets = await (await owner.PostAsJsonAsync(Groups, new { name = "Lưới" })).ReadAsync(HttpStatusCode.Created);
        var ropes = await (await owner.PostAsJsonAsync(Groups, new { name = "Dây" })).ReadAsync(HttpStatusCode.Created);
        await (await owner.PostAsJsonAsync(Groups, new { name = "lưới" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((0, 1), (nets.GetProperty("sortOrder").GetInt32(), ropes.GetProperty("sortOrder").GetInt32()));

        var item = await (await owner.PostAsJsonAsync(Items, new { name = "Lưới bao hàu", group = "Material", unit = "m", itemGroupId = nets.Id() })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Lưới", item.GetProperty("itemGroupName").GetString());
        var listed = (await owner.GetFromJsonAsync<JsonElement>(Groups)).EnumerateArray().ToList();
        Assert.Equal([("Lưới", 1), ("Dây", 0)], listed.Select(g => (g.GetProperty("name").GetString()!, g.GetProperty("itemCount").GetInt32())));

        var reordered = await (await owner.PostAsJsonAsync($"{Groups}/reorder", new { ids = new[] { ropes.Id(), nets.Id() } })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Dây", "Lưới"], reordered.EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        await (await owner.PostAsJsonAsync($"{Groups}/reorder", new { ids = new[] { ropes.Id() } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        await (await owner.DeleteAsync($"{Groups}/{nets.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Groups}/{ropes.Id()}")).StatusCode);
    }

    [Fact]
    public async Task Item_groups_nest_two_levels_count_their_children_and_keep_a_parent_until_it_is_empty()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var tools = await (await owner.PostAsJsonAsync(Groups, new { name = "Dụng cụ" })).ReadAsync(HttpStatusCode.Created);
        var nets = await (await owner.PostAsJsonAsync(Groups, new { name = "Lưới", parentId = tools.Id() })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(tools.Id(), nets.GetProperty("parentId").GetGuid());

        // Two levels at most, and a group is never its own parent.
        await (await owner.PostAsJsonAsync(Groups, new { name = "Lưới nhỏ", parentId = nets.Id() })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PutAsJsonAsync($"{Groups}/{tools.Id()}", new { name = "Dụng cụ", parentId = tools.Id(), rowVersion = tools.GetProperty("rowVersion").GetString() })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var other = await (await owner.PostAsJsonAsync(Groups, new { name = "Phao" })).ReadAsync(HttpStatusCode.Created);
        await (await owner.PutAsJsonAsync($"{Groups}/{tools.Id()}", new { name = "Dụng cụ", parentId = other.Id(), rowVersion = tools.GetProperty("rowVersion").GetString() })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        // A parent counts the items of its children, and filtering by it lists them.
        var item = await (await owner.PostAsJsonAsync(Items, new { name = "Lưới bao hàu", group = "Material", unit = "m", itemGroupId = nets.Id() })).ReadAsync(HttpStatusCode.Created);
        var listed = (await owner.GetFromJsonAsync<JsonElement>(Groups)).EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("itemCount").GetInt32());
        Assert.Equal((1, 1, 0), (listed["Dụng cụ"], listed["Lưới"], listed["Phao"]));
        var byParent = await owner.GetFromJsonAsync<JsonElement>($"{Items}/search?itemGroupId={tools.Id()}");
        Assert.Equal([item.Id()], byParent.GetProperty("items").EnumerateArray().Select(i => i.Id()));

        // A parent is deleted only without children and items.
        await (await owner.DeleteAsync($"{Groups}/{tools.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        await (await owner.DeleteAsync($"{Groups}/{nets.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06");
        await (await owner.DeleteAsync($"{Items}/{item.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Groups}/{nets.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Groups}/{tools.Id()}")).StatusCode);
    }

    [Fact]
    public async Task The_inventory_settings_default_to_a_fixed_cost_and_negative_stock_and_follow_the_row_version()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var settings = await owner.GetFromJsonAsync<JsonElement>(Settings);
        Assert.Equal(("Fixed", true), (settings.GetProperty("costMethod").GetString(), settings.GetProperty("allowNegativeQuantity").GetBoolean()));

        var saved = await (await owner.PutAsJsonAsync(Settings, new { costMethod = "Average", allowNegativeQuantity = false, rowVersion = settings.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Average", false), (saved.GetProperty("costMethod").GetString(), saved.GetProperty("allowNegativeQuantity").GetBoolean()));

        var stale = await owner.PutAsJsonAsync(Settings, new { costMethod = "Fixed", allowNegativeQuantity = true, rowVersion = settings.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await (await owner.PutAsJsonAsync(Settings, new { costMethod = "Weighted", allowNegativeQuantity = true, rowVersion = saved.GetProperty("rowVersion").GetString() })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task An_item_has_a_base_unit_and_units_with_factors_and_unique_codes()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var item = await (await owner.PostAsJsonAsync(Items, new
        {
            code = "001", name = "Nước mắm", group = "Other",
            units = new object[]
            {
                new { unitName = "chai", factor = 1, isBase = true },
                new { unitName = "thùng", factor = 12, isBase = false },
                new { unitName = "thau", factor = 6, isBase = false },
            },
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal("chai", item.GetProperty("unit").GetString());
        var units = item.GetProperty("units").EnumerateArray().ToList();
        // The base unit comes first, then by factor; codes are the item code, "-" and the letter, a digit appended on a clash (thùng and thau are both T).
        Assert.Equal(
            [("chai", 1m, "001-C", true), ("thau", 6m, "001-T", false), ("thùng", 12m, "001-T1", false)],
            units.Select(u => (u.GetProperty("name").GetString()!, u.GetProperty("factor").GetDecimal(), u.GetProperty("code").GetString()!, u.GetProperty("isBase").GetBoolean())));

        var clash = await owner.PostAsJsonAsync(Items, new { code = "001-C", name = "Khác", group = "Other", unit = "kg" });
        Assert.Equal("Code", (await clash.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("field").GetString());
        var unitClash = await owner.PostAsJsonAsync(Items, new
        {
            name = "Nước tương", group = "Other",
            units = new object[] { new { unitName = "chai", factor = 1, isBase = true, code = "001-T" } },
        });
        Assert.Equal("Unit code", (await unitClash.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("field").GetString());

        foreach (var factor in new[] { 0m, -2m, 0.0001m })
        {
            var invalid = await owner.PostAsJsonAsync(Items, new
            {
                name = $"Factor {factor}", group = "Other",
                units = new object[] { new { unitName = "kg", factor = 1, isBase = true }, new { unitName = "bao", factor, isBase = false } },
            });
            Assert.Equal("Factor", await invalid.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        var twoBases = await owner.PostAsJsonAsync(Items, new
        {
            name = "Two bases", group = "Other",
            units = new object[] { new { unitName = "kg", factor = 1, isBase = true }, new { unitName = "bao", factor = 5, isBase = true } },
        });
        Assert.Equal("Units", await twoBases.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_opening_quantity_is_on_hand_at_the_cost_given_and_is_corrected_only_while_it_is_the_only_movement()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;

        var item = await (await owner.PostAsJsonAsync(Items, new { name = "Dây buộc", group = "Material", unit = "kg", costPrice = 1000, openingQuantity = 5 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((5m, 5000m, 5m, true), (item.Money("onHand"), item.Money("stockValue"), item.Money("openingQuantity"), item.GetProperty("hasMovements").GetBoolean()));

        var edited = await owner.PutAsJsonAsync($"{Items}/{item.Id()}", new
        {
            code = item.GetProperty("code").GetString(), name = "Dây buộc", group = "Material", unit = "kg", costPrice = 1000, openingQuantity = 8,
            rowVersion = item.GetProperty("rowVersion").GetString(),
        });
        var corrected = await edited.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(8m, corrected.Money("onHand"));

        // Once a receipt has moved the stock, the quantity changes through documents only.
        await world.CompletedAsync(new LineBody(item.Id(), 2, 1000));
        var current = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item.Id()}");
        Assert.Equal(10m, current.Money("onHand"));
        var late = await owner.PutAsJsonAsync($"{Items}/{item.Id()}", new
        {
            code = current.GetProperty("code").GetString(), name = "Dây buộc", group = "Material", unit = "kg", costPrice = 1000, openingQuantity = 9,
            rowVersion = current.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("Opening quantity", await late.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_receipt_line_in_a_larger_unit_adds_base_units_sets_the_fixed_cost_and_leaves_with_the_receipt()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await (await owner.PostAsJsonAsync(Items, new
        {
            name = "Nước mắm", group = "Other",
            units = new object[] { new { unitName = "chai", factor = 1, isBase = true }, new { unitName = "thùng", factor = 12, isBase = false } },
        })).ReadAsync(HttpStatusCode.Created);
        var box = item.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("name").GetString() == "thùng");

        var receipt = await world.CompletedAsync(new LineBody(item.Id(), 2, 120_000, ItemUnitId: box.Id()));

        var line = receipt.LineViewOf(item.Id());
        Assert.Equal((2m, 120_000m, 24m, 12m, "thùng", "chai"), (line.Money("quantity"), line.Money("unitPrice"), line.Money("baseQuantity"), line.Money("unitFactor"), line.GetProperty("unit").GetString(), line.GetProperty("baseUnit").GetString()));
        Assert.Equal(240_000m, receipt.Money("total"));
        var afterReceipt = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item.Id()}");
        // 12 bottles per box: 24 bottles on hand at 10,000 a bottle, the cost the first receipt set.
        Assert.Equal((24m, 10_000m, 10_000m), (afterReceipt.Money("onHand"), afterReceipt.Money("costPrice"), afterReceipt.Money("lastPurchasePrice")));

        // A unit of another item is refused on the line.
        var other = await owner.CreateItemAsync("Other", "bag");
        var wrong = await owner.PostReceiptAsync(world.Body(new LineBody(other, 1, 100, ItemUnitId: box.Id())) with { Complete = true });
        Assert.Equal("Line 1 unit", await wrong.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        // Cancelling the receipt takes the quantity off again; the Fixed cost stays.
        await (await owner.CancelAsync(receipt.Id(), "Nhập nhầm")).ReadAsync(HttpStatusCode.OK);
        var afterCancel = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item.Id()}");
        Assert.Equal((0m, 10_000m), (afterCancel.Money("onHand"), afterCancel.Money("costPrice")));
        Assert.Equal(JsonValueKind.Null, afterCancel.GetProperty("lastPurchasePrice").ValueKind);
    }

    [Fact]
    public async Task The_average_cost_follows_receipts_and_returns()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var settings = await owner.GetFromJsonAsync<JsonElement>(Settings);
        await (await owner.PutAsJsonAsync(Settings, new { costMethod = "Average", allowNegativeQuantity = true, rowVersion = settings.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        var item = world.Material;

        await world.CompletedAsync(new LineBody(item, 10, 100));
        var first = await world.CompletedAsync(new LineBody(item, 10, 200));
        var averaged = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}");
        Assert.Equal((20m, 150m, 200m), (averaged.Money("onHand"), averaged.Money("costPrice"), averaged.Money("lastPurchasePrice")));

        // 10 of the second receipt go back at 200: (20 × 150 − 10 × 200) / 10.
        await world.ReturnAsync(first.Id(), first.LineOf(item), 10);
        var afterReturn = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}");
        Assert.Equal((10m, 100m), (afterReturn.Money("onHand"), afterReturn.Money("costPrice")));
    }

    [Fact]
    public async Task The_price_setup_of_a_receipt_shows_and_updates_the_cost_prices_of_its_items()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var receipt = await world.CompletedAsync(new LineBody(world.Material, 10, 5_000), new LineBody(world.Other, 4, 2_000));

        var setup = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}/price-setup");
        var rows = setup.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal((5_000m, 5_000m, 5_000m), (rows[0].Money("receiptPrice"), rows[0].Money("costPrice"), rows[0].Money("lastPurchasePrice")));

        var updated = await (await owner.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", new { items = new[] { new { itemId = world.Material, costPrice = 4_500m } } })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(4_500m, updated.GetProperty("lines")[0].Money("costPrice"));
        Assert.Equal(4_500m, (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Material}")).Money("costPrice"));

        // An item that is not on the receipt, or a negative price, is refused; staff may read the setup but not change it.
        await (await owner.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", new { items = new[] { new { itemId = world.Seed, costPrice = 1m } } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", new { items = new[] { new { itemId = world.Material, costPrice = -1m } } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var accountant = await api.AccountantAsync();
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup")).StatusCode);
        await (await accountant.PutAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/price-setup", new { items = new[] { new { itemId = world.Material, costPrice = 1m } } })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task The_item_list_filters_by_stock_state_group_and_search_and_sums_the_stock_value()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var group = await (await owner.PostAsJsonAsync(Groups, new { name = "Phao" })).ReadAsync(HttpStatusCode.Created);
        await owner.PostAsJsonAsync(Items, new { code = "PHAO-1", name = "Phao nhỏ", group = "Material", unit = "cái", itemGroupId = group.Id(), costPrice = 100, openingQuantity = 10, minQuantity = 20 });
        await owner.PostAsJsonAsync(Items, new { code = "PHAO-2", name = "Phao lớn", group = "Material", unit = "cái", itemGroupId = group.Id(), costPrice = 300, openingQuantity = 5, maxQuantity = 2 });
        await owner.PostAsJsonAsync(Items, new { code = "DAY-1", name = "Dây", group = "Other", unit = "m" });

        async Task<JsonElement> Page(string query) => await owner.GetFromJsonAsync<JsonElement>($"{Items}/search?{query}");
        string[] Codes(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray();

        Assert.Equal(["PHAO-2", "PHAO-1", "DAY-1"], Codes(await Page("")));
        Assert.Equal(["PHAO-2", "PHAO-1"], Codes(await Page($"itemGroupId={group.Id()}")));
        Assert.Equal(["PHAO-2", "PHAO-1"], Codes(await Page("stock=InStock")));
        Assert.Equal(["DAY-1"], Codes(await Page("stock=OutOfStock")));
        Assert.Equal(["PHAO-1"], Codes(await Page("stock=BelowMin")));
        Assert.Equal(["PHAO-2"], Codes(await Page("stock=AboveMax")));
        Assert.Equal(["PHAO-1"], Codes(await Page("search=nhỏ")));
        Assert.Equal(["PHAO-1"], Codes(await Page("search=PHAO-1-C")));
        Assert.Equal(["DAY-1"], Codes(await Page("group=Other")));
        Assert.Equal(["DAY-1", "PHAO-1", "PHAO-2"], Codes(await Page("sortBy=stockValue&sortDir=asc")));

        var all = await Page("");
        Assert.Equal(10 * 100m + 5 * 300m, all.GetProperty("summary").Money("stockValue"));
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        await (await owner.GetAsync($"{Items}/search?sortBy=nonsense")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task An_item_keeps_at_most_five_photos()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var item = await owner.CreateItemAsync("Other");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        for (var i = 0; i < 5; i++)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", $"photo{i}.png" } };
            var added = await (await owner.PostAsync($"{Items}/{item}/photos", form)).ReadAsync(HttpStatusCode.OK);
            Assert.Equal(i + 1, added.GetProperty("photos").GetArrayLength());
        }

        using var sixth = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "photo6.png" } };
        Assert.Equal("Photos", await (await owner.PostAsync($"{Items}/{item}/photos", sixth)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var view = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}");
        var removed = await (await owner.DeleteAsync($"{Items}/{item}/photos/{view.GetProperty("photos")[0].Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(4, removed.GetProperty("photos").GetArrayLength());
    }
}
