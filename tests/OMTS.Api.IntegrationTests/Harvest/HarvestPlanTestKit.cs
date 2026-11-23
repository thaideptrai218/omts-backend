using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>Harvest plan calls: the body as the clients send it, and what a plan leaves behind.</summary>
public static class HarvestPlanTestKit
{
    public const string PlanRoute = "/api/harvest-plans";

    /// <summary>One line of a plan: a raft (of any zone) and the lanes to harvest.</summary>
    public static object PlanLine(Guid raftId, int[] lanes, Guid? id = null, int? cages = null) =>
        new { id, raftId, laneNumbers = lanes, expectedCageCount = cages };

    /// <summary>
    /// A plan has no zone of its own: it takes the zones of the rafts of its lines. A plan serves at least one sales order (MSG-PLAN-07), so
    /// <see cref="CreatePlanAsync"/> links a new open order when the body names none.
    /// </summary>
    public static Dictionary<string, object?> PlanBody(params object[] lines) => new()
    {
        ["plannedAt"] = FarmingTestKit.Today.AddDays(1).ToDateTime(new TimeOnly(7, 30)).ToString("yyyy-MM-ddTHH:mm:ss") + "+07:00",
        ["salesOrderIds"] = Array.Empty<Guid>(),
        ["lines"] = lines,
    };

    private static readonly ConcurrentDictionary<Guid, HttpClient> RaftOwners = new();

    /// <summary>The Owner client that made a raft; the harvest kit plans the harvests of the raft with it.</summary>
    public static void RememberRaftOwner(Guid raftId, HttpClient owner) => RaftOwners[raftId] = owner;

    public static HttpClient? OwnerOfRaft(Guid raftId) => RaftOwners.TryGetValue(raftId, out var owner) ? owner : null;

    /// <summary>A product with a base (kg) unit that an order can name: an existing one, else a new one.</summary>
    private static readonly ConcurrentDictionary<HttpClient, Guid> OrderableProducts = new();

    private static async Task<Guid> OrderableProductAsync(this HttpClient owner)
    {
        // Found once per Owner client: every harvest of a test plans with an order, so looking the products up each time would slow the suite down.
        if (OrderableProducts.TryGetValue(owner, out var known))
        {
            return known;
        }

        var found = await owner.FindOrderableProductAsync();
        OrderableProducts[owner] = found;
        return found;
    }

    private static async Task<Guid> FindOrderableProductAsync(this HttpClient owner)
    {
        var products = await owner.GetFromJsonAsync<JsonElement>("/api/products");
        foreach (var product in products.EnumerateArray())
        {
            var id = product.GetProperty("id").GetGuid();
            var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/products/{id}");
            if (detail.TryGetProperty("units", out var units) && units.EnumerateArray().Any(u => u.GetProperty("isBase").GetBoolean()))
            {
                return id;
            }
        }

        return (await Sales.SalesCatalogueTestKit.CreateProductAsync(owner)).GetProperty("id").GetGuid();
    }

    /// <summary>A new open sales order (walk-in, one kg of the first product) for a plan to serve; one order has at most one active plan.</summary>
    public static async Task<Guid> OpenOrderAsync(this HttpClient owner)
    {
        var (orderId, _) = await Sales.SalesInvoiceTestKit.OrderOfAsync(owner, null, (await owner.OrderableProductAsync(), 1m));
        return orderId;
    }

    /// <summary>
    /// The plan the kit makes for a harvest a test records without naming a plan line (a harvest needs a plan since 18/11/2026, MSG-HARVEST-06):
    /// one line of the raft with the lanes the test sent, assigned, serving a new order. The id of its line, or null when the plan is refused
    /// (the harvest is then posted as the test wrote it, so the test sees the answer of the API).
    /// </summary>
    public static async Task<Guid?> PlannedLineForAsync(Guid raftId, IReadOnlyList<int> lanes)
    {
        if (OwnerOfRaft(raftId) is not { } owner || lanes.Count == 0)
        {
            return null;
        }

        var body = PlanBody(PlanLine(raftId, lanes.Distinct().ToArray())).WithPlanField("salesOrderIds", new[] { await owner.OpenOrderAsync() });
        var created = await owner.PostPlanAsync(body);
        if (created.StatusCode != HttpStatusCode.Created)
        {
            return null;
        }

        var plan = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        var assigned = await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/assign", new { });
        return assigned.IsSuccessStatusCode ? plan.LineId() : null;
    }

    public static Dictionary<string, object?> WithPlanField(this Dictionary<string, object?> body, string field, object? value)
    {
        body[field] = value;
        return body;
    }

    public static Task<HttpResponseMessage> PostPlanAsync(this HttpClient client, object body) => client.PostAsJsonAsync(PlanRoute, body);

    /// <summary>Saves the plan and returns the plan of the answer; a body that names no sales order gets a new open one (MSG-PLAN-07).</summary>
    public static async Task<JsonElement> CreatePlanAsync(this HttpClient client, object body)
    {
        if (body is Dictionary<string, object?> fields && fields.TryGetValue("salesOrderIds", out var ids) && ids is Guid[] { Length: 0 })
        {
            fields["salesOrderIds"] = new[] { await client.OpenOrderAsync() };
        }

        return (await (await client.PostPlanAsync(body)).ReadAsync(HttpStatusCode.Created)).GetProperty("plan");
    }

    public static async Task<JsonElement> GetPlanAsync(this HttpClient client, Guid planId) => await (await client.GetAsync($"{PlanRoute}/{planId}")).ReadAsync(HttpStatusCode.OK);

    /// <summary>A command on the plan (assign, finish); the answer is the saved plan.</summary>
    public static async Task<JsonElement> PlanCommandAsync(this HttpClient client, Guid planId, string command) =>
        (await (await client.PostAsJsonAsync($"{PlanRoute}/{planId}/{command}", new { })).ReadAsync(HttpStatusCode.OK)).GetProperty("plan");

    public static async Task<JsonElement> AssignedPlanAsync(this HttpClient client, object body)
    {
        var plan = await client.CreatePlanAsync(body);
        return await client.PlanCommandAsync(plan.Id(), "assign");
    }

    public static Task<HttpResponseMessage> PostSkipAsync(this HttpClient client, Guid planId, Guid lineId, int[] lanes, string? reason, string command = "skip-lanes") =>
        client.PostAsJsonAsync($"{PlanRoute}/{planId}/lines/{lineId}/{command}", new { laneNumbers = lanes, reason });

    /// <summary>Asserts a problem response with the status and MSG code, and returns its body.</summary>
    public static Task<JsonElement> PlanProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code) => Purchasing.PurchasingTestKit.ProblemAsync(response, status, code);

    /// <summary>The <c>field</c> of an MSG-GEN-01 problem.</summary>
    public static Task<string?> PlanFieldAsync(this HttpResponseMessage response, HttpStatusCode status, string code) => Purchasing.PurchasingTestKit.FieldAsync(response, status, code);

    public static Guid LineId(this JsonElement plan, int index = 0) => plan.GetProperty("lines")[index].Id();

    public static IEnumerable<string> AllowedActions(this JsonElement plan) => plan.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!);

    public static int StatusId(this JsonElement plan) => plan.GetProperty("statusId").GetInt32();

    public static string LaneState(this JsonElement plan, int lineIndex, int laneNumber) =>
        plan.GetProperty("lines")[lineIndex].GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber).Text("state");

    public static Task<int> PlanCountAsync(this AuthApiFactory api, string code) =>
        api.QueryAsync(db => db.HarvestPlans.AsNoTracking().CountAsync(p => p.Code == code));

    public static Task<List<HarvestPlanLane>> PlanLanesAsync(this AuthApiFactory api, Guid lineId) =>
        api.QueryAsync(db => db.HarvestPlanLanes.AsNoTracking().Where(l => l.LineId == lineId).ToListAsync());
}
