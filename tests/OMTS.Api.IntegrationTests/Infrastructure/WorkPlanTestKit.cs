using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OMTS.Application.Features.Authorization.Dtos;
using OMTS.Application.Features.Workforce.Dtos;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Fixture lập kế hoạch bằng API thật trước khi kiểm tra các bước phân công/chấm công cũ.</summary>
public static class WorkPlanTestKit
{
    public static async Task<WorkDayPlanView> SelfPlanAsync(HttpClient owner, Guid raftId, Guid jobId, DateOnly day,
        Guid? purchaseId = null, int? cages = null, int? capacity = null, DateOnly? pickup = null)
    {
        var who = await owner.GetFromJsonAsync<JsonElement>("/api/me");
        var accountId = who.GetProperty("accountId").GetGuid();
        WorkDayPlanView? existing = null;
        if (purchaseId is { } id)
        {
            var response = await owner.GetAsync($"/api/work-plans/purchase/{id}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            if (response.StatusCode != HttpStatusCode.NoContent) existing = await response.Content.ReadFromJsonAsync<WorkDayPlanView>();
        }
        else
            existing = (await owner.GetFromJsonAsync<List<WorkDayPlanView>>($"/api/work-plans?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}"))!
                .FirstOrDefault(p => p.PurchaseRequestId is null && p.RaftId == raftId && p.JobTypeId == jobId && p.ResponsibleAccountId == accountId);
        if (existing is not null) return existing;
        var created = await owner.PostAsJsonAsync("/api/work-plans", new SaveWorkDayPlan(raftId, jobId, day,
            accountId, Guid.NewGuid(), purchaseId, cages, capacity, pickup));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<WorkDayPlanView>())!;
    }

    public static async Task<HttpResponseMessage> AssignAsync(HttpClient client, object input)
    {
        var body = JsonSerializer.SerializeToNode(input)!.AsObject();
        var who = await client.GetFromJsonAsync<JsonElement>("/api/me");
        if (who.GetProperty("subjectType").GetString() == "CooperativeOwner")
        {
            var day = DateOnly.Parse(body["date"]!.GetValue<string>());
            var plan = await SelfPlanAsync(client, body["raftId"]!.GetValue<Guid>(), body["jobTypeId"]!.GetValue<Guid>(), day);
            body["workDayPlanId"] = JsonValue.Create(plan.Id);
        }
        return await client.PostAsJsonAsync("/api/workforce/assignments", body);
    }
}
