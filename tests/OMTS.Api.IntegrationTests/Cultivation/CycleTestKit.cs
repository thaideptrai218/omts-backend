using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The start (without a row version) and edit body of a cultivation cycle (CYCLE-01). A start without <paramref name="ProductId"/> is sent with the product of <see cref="CycleTestKit.CycleProductAsync"/>.</summary>
public sealed record CycleBody(
    DateOnly? StartDate = null, decimal? ExpectedSeedQuantity = null, DateOnly? ExpectedHarvestDate = null, decimal? TargetYieldKg = null, string? RowVersion = null, Guid? ProductId = null);

/// <summary>CYCLE-01 and CYCLE-05 calls.</summary>
public static class CycleTestKit
{
    private static readonly ConditionalWeakTable<HttpClient, Task<Guid?>> ProductOfClient = new();

    /// <summary>A product In Use for the starts of a cycle: the one named "Cycle test oyster", made once per client by the Owner; null for a client that cannot make one (its start then answers MSG-GEN-01 for the product).</summary>
    public static Task<Guid?> CycleProductAsync(this HttpClient client) => ProductOfClient.GetValue(client, MakeCycleProductAsync);

    private static async Task<Guid?> MakeCycleProductAsync(HttpClient client)
    {
        const string name = "Cycle test oyster";
        var made = await client.PostAsJsonAsync("/api/products", new { name, unitPrice = 60000 });
        if (made.StatusCode == HttpStatusCode.Created)
        {
            var saved = await made.Content.ReadFromJsonAsync<JsonElement>();
            return (saved.TryGetProperty("product", out var product) ? product : saved).GetProperty("id").GetGuid();
        }

        var listed = await client.GetAsync("/api/products");
        return listed.IsSuccessStatusCode
            ? (await listed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Where(p => p.GetProperty("name").GetString() == name).Select(p => (Guid?)p.GetProperty("id").GetGuid()).FirstOrDefault()
            : null;
    }

    public static async Task<HttpResponseMessage> PostCycleAsync(this HttpClient client, Guid raftId, CycleBody? body = null)
    {
        body ??= new CycleBody();
        return await client.PostAsJsonAsync($"/api/rafts/{raftId}/cultivation-cycles", body with { ProductId = body.ProductId ?? await client.CycleProductAsync() });
    }

    /// <summary>Starts a cycle and returns the saved cycle, the <c>cycle</c> of the answer.</summary>
    public static async Task<JsonElement> StartCycleAsync(this HttpClient client, Guid raftId, CycleBody? body = null) =>
        (await (await client.PostCycleAsync(raftId, body)).ReadAsync(HttpStatusCode.Created)).GetProperty("cycle");

    public static Task<HttpResponseMessage> PutCycleAsync(this HttpClient client, Guid cycleId, CycleBody body) =>
        client.PutAsJsonAsync($"/api/cultivation-cycles/{cycleId}", body);

    /// <summary>Edits a cycle and returns the saved cycle, the <c>cycle</c> of the answer.</summary>
    public static async Task<JsonElement> EditCycleAsync(this HttpClient client, Guid cycleId, CycleBody body) =>
        (await (await client.PutCycleAsync(cycleId, body)).ReadAsync(HttpStatusCode.OK)).GetProperty("cycle");

    public static Task<HttpResponseMessage> CancelCycleAsync(this HttpClient client, Guid cycleId, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/cultivation-cycles/{cycleId}/cancel", new { rowVersion });

    public static Task<JsonElement> ProgressAsync(this HttpClient client, Guid raftId, string query = "") =>
        client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/progress{query}");

    public static async Task<JsonElement> ZoneRowAsync(this HttpClient client, Guid zoneId, Guid raftId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/cultivation-cycles")).EnumerateArray().Single(r => r.GetProperty("raftId").GetGuid() == raftId);

    public static DateOnly Date(this JsonElement element, string property) => DateOnly.Parse(element.GetProperty(property).GetString()!);

    public static bool Flag(this JsonElement element, string property) => element.GetProperty(property).GetBoolean();

    public static bool IsNull(this JsonElement element, string property) => element.GetProperty(property).ValueKind == JsonValueKind.Null;
}
