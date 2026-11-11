using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-08 of SRS 27/10: code and name unique with discontinued items included (GBR-03, BR-PURCHASE-10), one unit
/// per item (Seed items included), the code proposal by type (GI/VT/HK000001), groups Seed/Material/Other, edit only while In Use, and Resume.
/// Tests that need an empty catalogue start their own host.
/// </summary>
public class FarmingInputItemSrsRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Items = "/api/farming-input-items";

    [Fact]
    public async Task The_name_or_code_of_a_discontinued_item_stays_taken_and_MSG_GEN_02_names_the_existing_item()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var code = NewCode();
        var name = $"Phao xốp {code}";
        var first = await CreatedAsync(owner, new { code, name, group = "Material", unit = "cái" });
        await (await owner.PostAsync($"{Items}/{first}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var sameName = await owner.PostAsJsonAsync(Items, new { code = NewCode(), name = name.ToUpperInvariant(), group = "Other", unit = "kg" });
        var problem = await sameName.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((first, "Name"), (problem.GetProperty("existingId").GetGuid(), problem.GetProperty("field").GetString()));
        Assert.Equal("Farming input item already exists. Your entries are kept: change them, or open the existing record.", problem.GetProperty("detail").GetString());
        var sameCode = await owner.PostAsJsonAsync(Items, new { code = code.ToUpperInvariant(), name = $"Another {code}", group = "Other", unit = "kg" });
        var codeProblem = await sameCode.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((first, "Code"), (codeProblem.GetProperty("existingId").GetGuid(), codeProblem.GetProperty("field").GetString()));
        // Both clash with the same item: the code is named.
        var both = await owner.PostAsJsonAsync(Items, new { code, name, group = "Material", unit = "cái" });
        Assert.Equal("Code", (await both.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("field").GetString());

        // Editing another item to the same name is refused the same way.
        var second = await owner.CreateItemAsync("Other");
        var view = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{second}");
        var edit = await owner.PutAsJsonAsync($"{Items}/{second}", new
        {
            code = view.GetProperty("code").GetString(), name, group = "Other", unit = "kg", rowVersion = view.GetProperty("rowVersion").GetString(),
        });
        var editProblem = await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((first, "Name"), (editProblem.GetProperty("existingId").GetGuid(), editProblem.GetProperty("field").GetString()));
        Assert.Equal(1, await factory.QueryAsync(db => db.FarmingInputItems.CountAsync(i => i.Name == name)));
    }

    [Fact]
    public async Task Seed_items_may_count_in_different_units_and_an_item_has_a_base_unit_with_a_code()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();

        var con = await (await owner.PostAsJsonAsync(Items, new { code = "VT001", name = "Hàu giống 3 cm", group = "Seed", unit = "con" })).ReadAsync(HttpStatusCode.Created);
        var kg = await (await owner.PostAsJsonAsync(Items, new { code = "VT002", name = "Hàu giống 5 cm", group = "Seed", unit = "kg" })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal("con", con.GetProperty("unit").GetString());
        Assert.Equal("kg", kg.GetProperty("unit").GetString());
        var units = con.GetProperty("units").EnumerateArray().ToList();
        Assert.Single(units);
        Assert.Equal(("con", 1m, "VT001-C", true), (units[0].GetProperty("name").GetString(), units[0].GetProperty("factor").GetDecimal(), units[0].GetProperty("code").GetString(), units[0].GetProperty("isBase").GetBoolean()));
    }

    [Fact]
    public async Task The_only_seed_item_may_change_its_unit_while_it_is_unused()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var seed = await CreatedAsync(owner, new { code = "VT001", name = "Hàu giống", group = "Seed", unit = "kg" });
        var view = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{seed}");

        var edited = await (await owner.PutAsJsonAsync($"{Items}/{seed}", new
        {
            code = "VT001", name = "Hàu giống", group = "Seed", unit = "con",
            units = new[] { new { id = (Guid?)null, unitId = (Guid?)null, unitName = "con", factor = 1m, code = (string?)null, isBase = true } },
            rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("con", edited.GetProperty("unit").GetString());
        Assert.Equal("con", edited.GetProperty("units")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_proposed_code_follows_the_type_and_the_Owner_may_replace_it()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        Assert.Equal("GI000001", (await ProposalAsync(owner, "Seed")).GetProperty("code").GetString());
        Assert.Equal("VT000001", (await ProposalAsync(owner, "Material")).GetProperty("code").GetString());
        Assert.Equal("HK000001", (await ProposalAsync(owner, "Other")).GetProperty("code").GetString());

        // An empty code takes the next one of the type's sequence; the numbers of the other types are not spent.
        var first = await (await owner.PostAsJsonAsync(Items, new { name = "Phao nhựa", group = "Material", unit = "cái" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("VT000001", first.GetProperty("code").GetString());
        Assert.Equal("VT000002", (await ProposalAsync(owner, "Material")).GetProperty("code").GetString());
        Assert.Equal("GI000001", (await ProposalAsync(owner, "Seed")).GetProperty("code").GetString());

        // A code typed by the Owner is kept as typed and is unique regardless of case; the code of a unit counts too.
        await owner.CreateItemAsync("Other", code: "PHAO-01");
        var clash = await owner.PostAsJsonAsync(Items, new { code = "phao-01", name = "Khác", group = "Other", unit = "kg" });
        Assert.Equal("Code", (await clash.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("field").GetString());
        var unitCode = await owner.PostAsJsonAsync(Items, new { code = "VT000001-C", name = "Dây", group = "Material", unit = "cuộn" });
        Assert.Equal("Code", (await unitCode.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("field").GetString());

        // The proposal belongs to the Add item form of the Owner.
        var (zone, _) = await api.ZoneWithRaftAsync();
        await (await (await api.ZoneManagerAsync(zone)).GetAsync($"{Items}/next-code?group=Seed")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }
    [Fact]
    public async Task A_discontinued_item_is_not_edited_until_it_is_resumed()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var id = await owner.CreateItemAsync("Other");
        var view = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{id}");
        var code = view.GetProperty("code").GetString();
        object Body(string name) => new { code, name, group = "Other", unit = "kg", rowVersion = view.GetProperty("rowVersion").GetString() };
        await (await owner.PostAsync($"{Items}/{id}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var edit = await owner.PutAsJsonAsync($"{Items}/{id}", Body($"Renamed {code}"));

        var problem = await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal($"Farming input item {code} is Discontinued and cannot be used for this action.", problem.GetProperty("detail").GetString());
        Assert.Equal($"Other item {code}", (await factory.QueryAsync(db => db.FarmingInputItems.SingleAsync(i => i.Id == id))).Name);

        var resumed = await (await owner.PostAsync($"{Items}/{id}/resume", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("In Use", resumed.GetProperty("status").GetString());
        await (await owner.PostAsync($"{Items}/{id}/resume", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var edited = await (await owner.PutAsJsonAsync($"{Items}/{id}", Body($"Renamed {code}"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal($"Renamed {code}", edited.GetProperty("name").GetString());
        Assert.Equal(
            ["FarmingInputItem.Created", "FarmingInputItem.Discontinued", "FarmingInputItem.Reactivated", "FarmingInputItem.Updated"],
            await factory.AuditActionsAsync(id));
    }

    [Fact]
    public async Task The_groups_are_Seed_Material_and_Other_in_JSON()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var code = NewCode();
        var material = await (await owner.PostAsJsonAsync(Items, new { code, name = $"Lưới {code}", group = "Material", unit = "m" })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Material", material.GetProperty("group").GetString());

        // The group name of the old list is not accepted any more.
        var old = await owner.PostAsJsonAsync(Items, new { code = NewCode(), name = $"Cọc {code}", group = "RaftMaterial", unit = "cái" });
        await old.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        var materials = await owner.GetFromJsonAsync<JsonElement>($"{Items}?group=Material");
        Assert.Contains(materials.EnumerateArray(), i => i.Id() == material.Id());
        Assert.All(materials.EnumerateArray(), i => Assert.Equal("Material", i.GetProperty("group").GetString()));
    }

    private static async Task<Guid> CreatedAsync(HttpClient owner, object body) =>
        (await (await owner.PostAsJsonAsync(Items, body)).ReadAsync(HttpStatusCode.Created)).Id();

    private static async Task<JsonElement> ProposalAsync(HttpClient owner, string group) =>
        await (await owner.GetAsync($"{Items}/next-code?group={group}")).ReadAsync(HttpStatusCode.OK);
}
