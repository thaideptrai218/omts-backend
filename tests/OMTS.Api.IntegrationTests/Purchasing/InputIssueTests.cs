using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The issues of input items: use issues in a zone and disposals, their quantities on the movement ledger, the check for a negative stock per item and seed lot, who may do what, the deletion
/// of an item or of a seed lot, and the issue types. Each test starts its own host.
/// </summary>
public sealed class InputIssueTests
{
    private const string Issues = "/api/input-issues";
    private const string Items = "/api/farming-input-items";

    private static async Task<Guid> StockedItemAsync(HttpClient owner, string name, decimal quantity, decimal cost = 100m, string group = "Material", string unit = "m") =>
        (await (await owner.PostAsJsonAsync(Items, new { name, group, unit, costPrice = cost, openingQuantity = quantity })).ReadAsync(HttpStatusCode.Created)).Id();

    private static async Task<Guid> TypeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/input-issue-types")).EnumerateArray().First().Id();

    private static object Use(Guid zone, Guid raft, Guid type, bool complete, params object[] lines) => new { kind = "Use", zoneId = zone, raftId = raft, issueTypeId = type, complete, lines };

    private static async Task<decimal> OnHandAsync(HttpClient client, Guid item) =>
        (await client.GetFromJsonAsync<JsonElement>($"{Items}/{item}")).Money("onHand");

    private static async Task AllowNegativeAsync(HttpClient owner, bool allow)
    {
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/input-inventory-settings");
        await (await owner.PutAsJsonAsync("/api/input-inventory-settings", new { costMethod = "Fixed", allowNegativeQuantity = allow, rowVersion = settings.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_completed_use_issue_takes_its_quantity_out_at_the_cost_and_cancelling_it_puts_it_back()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Dây buộc", 10m, 100m);
        var type = await TypeAsync(owner);

        var issue = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 4 }))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Completed", 400m, "XD000001"), (issue.GetProperty("status").GetString(), issue.Money("totalValue"), issue.GetProperty("code").GetString()));
        Assert.Equal(6m, await OnHandAsync(owner, item));
        var movements = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}/movements");
        var latest = movements.GetProperty("items")[0];
        Assert.Equal(("Issue", -4m, 6m, "XD000001"), (latest.GetProperty("kind").GetString(), latest.Money("quantity"), latest.Money("balanceAfter"), latest.GetProperty("documentCode").GetString()));

        await (await owner.PostAsJsonAsync($"{Issues}/{issue.Id()}/cancel", new { reason = "Nhập nhầm" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(10m, await OnHandAsync(owner, item));
        await (await owner.PostAsJsonAsync($"{Issues}/{issue.Id()}/cancel", new { reason = "Again" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_draft_holds_nothing_is_removed_and_completes_later()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 10m);
        var type = await TypeAsync(owner);

        var draft = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, false, new { itemId = item, quantity = 3 }))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("Draft", 10m), (draft.GetProperty("status").GetString(), await OnHandAsync(owner, item)));
        var completed = await (await owner.PostAsJsonAsync($"{Issues}/{draft.Id()}/complete", new { })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Completed", 7m), (completed.GetProperty("status").GetString(), await OnHandAsync(owner, item)));

        var other = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, false, new { itemId = item, quantity = 1 }))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Issues}/{other.Id()}")).StatusCode);
        await (await owner.DeleteAsync($"{Issues}/{draft.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task Completing_below_zero_is_refused_when_the_cooperative_does_not_allow_it()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 10m);
        var type = await TypeAsync(owner);

        // Allowed by default.
        var first = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 12 }))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(-2m, await OnHandAsync(owner, item));
        await (await owner.PostAsJsonAsync($"{Issues}/{first.Id()}/cancel", new { reason = "Test" })).ReadAsync(HttpStatusCode.OK);

        await AllowNegativeAsync(owner, false);
        var refused = await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 12 }));
        var problem = await refused.ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
        Assert.Equal(item, problem.GetProperty("itemId").GetGuid());
        Assert.Equal(10m, await OnHandAsync(owner, item));

        // A draft may be saved; completing it fails the same way, and editing a Completed issue is judged by its result.
        var draft = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, false, new { itemId = item, quantity = 12 }))).ReadAsync(HttpStatusCode.Created);
        await (await owner.PostAsJsonAsync($"{Issues}/{draft.Id()}/complete", new { })).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
        var ok = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 10 }))).ReadAsync(HttpStatusCode.Created);
        var edit = await owner.PutAsJsonAsync($"{Issues}/{ok.Id()}", new { kind = "Use", zoneId = world.Zone.Id, raftId = world.Raft.Id, issueTypeId = type, rowVersion = ok.GetProperty("rowVersion").GetString(), lines = new[] { new { itemId = item, quantity = 11 } } });
        await edit.ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
        Assert.Equal(0m, await OnHandAsync(owner, item));
    }

    [Fact]
    public async Task Editing_a_completed_issue_writes_its_quantities_again()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 10m);
        var type = await TypeAsync(owner);
        var issue = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 4 }))).ReadAsync(HttpStatusCode.Created);

        var edited = await (await owner.PutAsJsonAsync($"{Issues}/{issue.Id()}", new
        {
            kind = "Use", zoneId = world.Zone.Id, raftId = world.Raft.Id, issueTypeId = type, rowVersion = issue.GetProperty("rowVersion").GetString(), lines = new[] { new { itemId = item, quantity = 6 } },
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Completed", 4m), (edited.GetProperty("status").GetString(), await OnHandAsync(owner, item)));
        var all = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}/movements?includeReversals=true");
        Assert.Contains(all.GetProperty("items").EnumerateArray(), m => m.GetProperty("kind").GetString() == "Reversal");
        var net = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{item}/movements");
        Assert.DoesNotContain(net.GetProperty("items").EnumerateArray(), m => m.GetProperty("kind").GetString() == "Reversal");
    }

    [Fact]
    public async Task A_seed_line_draws_from_its_lot_or_from_the_quantity_that_has_no_lot()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        await AllowNegativeAsync(owner, false);
        var type = await TypeAsync(owner);
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 1_000));
        var lotId = receipt.LineOf(world.Seed);

        var lots = await owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Seed}/seed-lots");
        Assert.Equal((lotId, 100m, 0m), (lots.GetProperty("suggestedLotId").GetGuid(), lots.GetProperty("lots")[0].Money("onHand"), lots.Money("unlottedQuantity")));

        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = world.Seed, quantity = 30, seedLotLineId = lotId }))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(70m, (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Seed}/seed-lots")).GetProperty("lots")[0].Money("onHand"));
        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = world.Seed, quantity = 80, seedLotLineId = lotId }))).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");

        // Without a lot the line draws from the quantity that has none: nothing here.
        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = world.Seed, quantity = 1 }))).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-01");
        // A lot of another item, or a lot named for a material item, is invalid.
        var other = await world.CompletedAsync(new LineBody(world.Material, 5, 10));
        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = world.Seed, quantity = 1, seedLotLineId = other.LineOf(world.Material) }))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = world.Material, quantity = 1, seedLotLineId = lotId }))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task A_zone_manager_works_on_the_use_issues_of_its_zones_only_and_the_accountant_only_reads()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var (otherZone, otherRaft) = await api.ZoneWithRaftAsync();
        var manager = await api.ZoneManagerAsync(world.Zone);
        var accountant = await api.AccountantAsync();
        var item = await StockedItemAsync(owner, "Phao", 50m);
        var type = await TypeAsync(owner);

        var mine = await (await manager.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 2 }))).ReadAsync(HttpStatusCode.Created);
        await (await manager.PostAsJsonAsync(Issues, Use(otherZone.Id, otherRaft.Id, type, true, new { itemId = item, quantity = 2 }))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await manager.PostAsJsonAsync(Issues, new { kind = "Disposal", reason = "Hỏng", complete = true, lines = new[] { new { itemId = item, quantity = 1 } } })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        var elsewhere = await (await owner.PostAsJsonAsync(Issues, Use(otherZone.Id, otherRaft.Id, type, true, new { itemId = item, quantity = 1 }))).ReadAsync(HttpStatusCode.Created);
        var disposal = await (await owner.PostAsJsonAsync(Issues, new { kind = "Disposal", reason = "Hỏng", complete = true, lines = new[] { new { itemId = item, quantity = 1 } } })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("XH000001", disposal.GetProperty("code").GetString());

        var seenByManager = (await manager.GetFromJsonAsync<JsonElement>(Issues)).GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
        Assert.Equal([mine.Id()], seenByManager);
        await (await manager.GetAsync($"{Issues}/{elsewhere.Id()}")).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.GetAsync($"{Issues}/{disposal.Id()}")).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await manager.PostAsJsonAsync($"{Issues}/{mine.Id()}/cancel", new { reason = "Nhầm" })).ReadAsync(HttpStatusCode.OK);

        var seenByAccountant = (await accountant.GetFromJsonAsync<JsonElement>(Issues)).GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
        Assert.Contains(disposal.Id(), seenByAccountant);
        Assert.Contains(elsewhere.Id(), seenByAccountant);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"{Issues}/export")).StatusCode);
        await (await accountant.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 1 }))).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await accountant.PostAsJsonAsync($"{Issues}/{elsewhere.Id()}/cancel", new { reason = "x" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task The_issue_list_filters_by_status_kind_and_time_and_sums_the_completed_values()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 50m, 10m);
        var type = await TypeAsync(owner);
        await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, true, new { itemId = item, quantity = 2 }));
        await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, false, new { itemId = item, quantity = 3 }));
        await owner.PostAsJsonAsync(Issues, new { kind = "Disposal", reason = "Hỏng", complete = true, lines = new[] { new { itemId = item, quantity = 4 } } });

        async Task<JsonElement> Page(string query) => await owner.GetFromJsonAsync<JsonElement>($"{Issues}?{query}");
        Assert.Equal(3, (await Page("")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Page("status=Completed")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Page("kind=Disposal")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await Page($"zoneId={world.Zone.Id}&status=Draft")).GetProperty("total").GetInt32());
        Assert.Equal(60m, (await Page("")).GetProperty("summary").Money("totalValue"));
        Assert.Equal(3, (await Page($"from={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)):yyyy-MM-dd}")).GetProperty("total").GetInt32());
        await (await owner.GetAsync($"{Issues}?sortBy=nonsense")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task Deleting_an_item_writes_off_what_is_left_and_hides_it_and_deleting_a_seed_lot_writes_off_the_lot()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 5m, 10m);

        var deleted = await (await owner.DeleteAsync($"{Items}/{item}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(5m, deleted.Money("disposedQuantity"));
        var disposal = await owner.GetFromJsonAsync<JsonElement>($"{Issues}/{deleted.GetProperty("disposalId").GetGuid()}");
        Assert.Equal(("Disposal", "Completed", "Xóa vật tư"), (disposal.GetProperty("kind").GetString(), disposal.GetProperty("status").GetString(), disposal.GetProperty("reason").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{Items}/{item}")).StatusCode);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<JsonElement>($"{Items}/search")).GetProperty("items").EnumerateArray(), i => i.Id() == item);

        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 40, 1_000));
        var lotId = receipt.LineOf(world.Seed);
        var lot = await (await owner.DeleteAsync($"{Items}/{world.Seed}/seed-lots/{lotId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(40m, lot.Money("disposedQuantity"));
        Assert.Equal(0m, (await owner.GetFromJsonAsync<JsonElement>($"{Items}/{world.Seed}/seed-lots")).GetProperty("lots")[0].Money("onHand"));
        await (await owner.DeleteAsync($"{Items}/{world.Seed}/seed-lots/{lotId}")).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task A_use_issue_belongs_to_one_zone_and_one_raft_of_that_zone()
    {
        using var api = new AuthApiFactory();
        var world = await api.WorldAsync();
        var owner = world.Owner;
        var item = await StockedItemAsync(owner, "Phao", 10m);
        var type = await TypeAsync(owner);
        var (_, otherRaft) = await api.ZoneWithRaftAsync();

        await (await owner.PostAsJsonAsync(Issues, new { kind = "Use", zoneId = world.Zone.Id, issueTypeId = type, complete = false, lines = new[] { new { itemId = item, quantity = 1 } } })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, otherRaft.Id, type, false, new { itemId = item, quantity = 1 }))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var saved = await (await owner.PostAsJsonAsync(Issues, Use(world.Zone.Id, world.Raft.Id, type, false, new { itemId = item, quantity = 1 }))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(world.Raft.Id, saved.GetProperty("raftId").GetGuid());
        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonElement>($"{Issues}?raftId={world.Raft.Id}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_system_issue_types_are_protected_and_the_Owner_manages_the_others()
    {
        using var api = new AuthApiFactory();
        var owner = await api.PurchasingOwnerAsync();
        var types = (await owner.GetFromJsonAsync<JsonElement>("/api/input-issue-types")).EnumerateArray().ToList();
        Assert.Equal(new[] { "Dùng chung cho vùng", "Sửa chữa bè", "Thả giống" }, types.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.All(types, t => Assert.True(t.GetProperty("isSystem").GetBoolean()));

        await (await owner.PutAsJsonAsync($"/api/input-issue-types/{types[0].Id()}", new { name = "Khác", rowVersion = types[0].GetProperty("rowVersion").GetString() })).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-02");
        await (await owner.DeleteAsync($"/api/input-issue-types/{types[1].Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-INPUT-02");

        var own = await (await owner.PostAsJsonAsync("/api/input-issue-types", new { name = "Thu hoạch" })).ReadAsync(HttpStatusCode.Created);
        await (await owner.PostAsJsonAsync("/api/input-issue-types", new { name = "THU HOẠCH" })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        var renamed = await (await owner.PutAsJsonAsync($"/api/input-issue-types/{own.Id()}", new { name = "Thu hoạch hàu", rowVersion = own.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Thu hoạch hàu", renamed.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/input-issue-types/{own.Id()}")).StatusCode);
    }
}
