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

    /// <summary>A plan has no zone of its own: it takes the zones of the rafts of its lines.</summary>
    public static Dictionary<string, object?> PlanBody(params object[] lines) => new()
    {
        ["plannedAt"] = FarmingTestKit.Today.AddDays(1).ToDateTime(new TimeOnly(7, 30)).ToString("yyyy-MM-ddTHH:mm:ss") + "+07:00",
        ["salesOrderIds"] = Array.Empty<Guid>(),
        ["lines"] = lines,
    };

    public static Dictionary<string, object?> WithPlanField(this Dictionary<string, object?> body, string field, object? value)
    {
        body[field] = value;
        return body;
    }

    public static Task<HttpResponseMessage> PostPlanAsync(this HttpClient client, object body) => client.PostAsJsonAsync(PlanRoute, body);

    /// <summary>Saves the plan and returns the plan of the answer.</summary>
    public static async Task<JsonElement> CreatePlanAsync(this HttpClient client, object body) =>
        (await (await client.PostPlanAsync(body)).ReadAsync(HttpStatusCode.Created)).GetProperty("plan");

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
