using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-08 of SRS 27/10: code and name unique with discontinued items included (GBR-03, BR-PURCHASE-10), one unit
/// for all Seed items, the code proposal VT001…, groups Seed/Material/Other, edit only while In Use, and Resume.
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
    public async Task Every_seed_item_takes_the_unit_of_the_first_one_and_the_proposal_fills_it_in()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        Assert.Equal(JsonValueKind.Null, (await ProposalAsync(owner)).GetProperty("seedUnit").ValueKind);

        // The first Seed item chooses the unit.
        var first = await CreatedAsync(owner, new { code = "VT001", name = "Hàu giống 3 cm", group = "Seed", unit = "con" });
        Assert.Equal("con", (await ProposalAsync(owner)).GetProperty("seedUnit").GetString());

        var kg = await owner.PostAsJsonAsync(Items, new { code = "VT002", name = "Hàu giống 5 cm", group = "Seed", unit = "kg" });
        var problem = await kg.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal(("Unit", "con"), (problem.GetProperty("field").GetString(), problem.GetProperty("seedUnit").GetString()));
        await CreatedAsync(owner, new { code = "VT002", name = "Hàu giống 5 cm", group = "Seed", unit = "con" });

        // Other groups choose their own unit; a Material item turned into a Seed item must take the seed unit.
        var rope = await CreatedAsync(owner, new { code = "VT003", name = "Dây thừng 14 mm", group = "Material", unit = "cuộn" });
        var ropeView = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{rope}");
        var toSeed = await owner.PutAsJsonAsync($"{Items}/{rope}", new
        {
            code = "VT003", name = "Dây thừng 14 mm", group = "Seed", unit = "cuộn", rowVersion = ropeView.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("Unit", await toSeed.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        // Discontinued Seed items keep their history, so they still set the unit.
        var seeds = await api.QueryAsync(db => db.FarmingInputItems.Where(i => i.Group == Domain.Enums.InputItemGroup.Seed).Select(i => i.Id).ToListAsync());
        Assert.Equal(2, seeds.Count);
        Assert.Contains(first, seeds);
        foreach (var seed in seeds)
        {
            await (await owner.PostAsync($"{Items}/{seed}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        }

        var afterDiscontinue = await owner.PostAsJsonAsync(Items, new { code = "VT004", name = "Hàu giống Cổ Trai", group = "Seed", unit = "kg" });
        Assert.Equal("Unit", await afterDiscontinue.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
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
            code = "VT001", name = "Hàu giống", group = "Seed", unit = "con", rowVersion = view.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("con", edited.GetProperty("unit").GetString());
        Assert.Equal("con", (await ProposalAsync(owner)).GetProperty("seedUnit").GetString());
    }

    [Fact]
    public async Task The_proposed_code_follows_VT001_keeps_discontinued_codes_reserved_and_may_be_replaced()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        Assert.Equal("VT001", (await ProposalAsync(owner)).GetProperty("code").GetString());

        await owner.CreateItemAsync("Other", code: "VT001");
        Assert.Equal("VT002", (await ProposalAsync(owner)).GetProperty("code").GetString());

        // The Owner may enter a code of their own; it does not move the proposal.
        await owner.CreateItemAsync("Other", code: "PHAO-01");
        Assert.Equal("VT002", (await ProposalAsync(owner)).GetProperty("code").GetString());

        // A code of the pattern in another case counts, and stays reserved once discontinued.
        var high = await owner.CreateItemAsync("Other", code: "vt012");
        await (await owner.PostAsync($"{Items}/{high}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("VT013", (await ProposalAsync(owner)).GetProperty("code").GetString());

        // The proposal belongs to the Add item form of the Owner.
        var (zone, _) = await api.ZoneWithRaftAsync();
        await (await (await api.ZoneManagerAsync(zone)).GetAsync($"{Items}/next-code")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
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

    private static async Task<JsonElement> ProposalAsync(HttpClient owner) =>
        await (await owner.GetAsync($"{Items}/next-code")).ReadAsync(HttpStatusCode.OK);
}
