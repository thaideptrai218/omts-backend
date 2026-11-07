using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The multipart fields of a recorded harvest; by default one lane, eight cages, no estimate and no photo.</summary>
public sealed record HarvestFields
{
    public IReadOnlyList<string> Lanes { get; init; } = ["1"];
    public IReadOnlyList<string> Finished { get; init; } = [];
    public string? Cages { get; init; } = "8";
    public string? EstimatedKg { get; init; }
    public IReadOnlyList<byte[]> Photos { get; init; } = [];
    public string PhotoField { get; init; } = "photos";

    public static HarvestFields OfLanes(params int[] lanes) => new() { Lanes = lanes.Select(n => n.ToString()).ToList() };

    public HarvestFields FinishedLanes(params int[] lanes) => this with { Finished = lanes.Select(n => n.ToString()).ToList() };

    public MultipartFormDataContent ToForm()
    {
        var form = new MultipartFormDataContent();
        foreach (var lane in Lanes)
        {
            form.Add(new StringContent(lane), "laneNumbers");
        }

        foreach (var lane in Finished)
        {
            form.Add(new StringContent(lane), "finishedLaneNumbers");
        }

        if (Cages is not null)
        {
            form.Add(new StringContent(Cages), "cageCount");
        }

        if (EstimatedKg is not null)
        {
            form.Add(new StringContent(EstimatedKg), "estimatedKg");
        }

        foreach (var photo in Photos)
        {
            form.Add(FarmingTestKit.FileContent(photo, "image/jpeg"), PhotoField, "harvest.jpg");
        }

        return form;
    }
}

/// <summary>A raft whose lanes have seed in an Open cycle, by a new Owner.</summary>
public sealed record SeededRaft(Actor Owner, Guid ZoneId, Guid RaftId, Guid CycleId, SeedLotMade Lot, JsonElement Session);

/// <summary>HARVEST-01 and HARVEST-03 calls and what they should have left behind.</summary>
public static class HarvestTestKit
{
    public static Task<HttpResponseMessage> PostHarvestAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        client.PostAsync($"/api/rafts/{raftId}/harvest-lots", (fields ?? new HarvestFields()).ToForm());

    /// <summary>Records a harvest and returns the whole answer.</summary>
    public static async Task<JsonElement> RecordHarvestAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        await (await client.PostHarvestAsync(raftId, fields)).ReadAsync(HttpStatusCode.Created);

    /// <summary>Records a harvest and returns the saved lot.</summary>
    public static async Task<JsonElement> HarvestLotAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        (await client.RecordHarvestAsync(raftId, fields)).GetProperty("lot");

    public static Task<JsonElement> HarvestScreenAsync(this HttpClient client, Guid raftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/harvest-lots");

    public static Task<JsonElement> GetHarvestLotAsync(this HttpClient client, Guid lotId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{lotId}");

    public static Task<HttpResponseMessage> PutHarvestSettingsAsync(this HttpClient client, decimal? average, int? days, string? rowVersion) =>
        client.PutAsJsonAsync("/api/harvest-settings", new { averageKgPerCage = average, daysBeforeAutoClose = days, rowVersion });

    public static Task<JsonElement> HarvestSettingsAsync(this HttpClient client) =>
        client.GetFromJsonAsync<JsonElement>("/api/harvest-settings");

    /// <summary>Sets the two harvest settings with the current version.</summary>
    public static async Task SetHarvestSettingsAsync(this HttpClient owner, decimal average, int days)
    {
        var current = await owner.HarvestSettingsAsync();
        (await owner.PutHarvestSettingsAsync(average, days, current.Text("rowVersion"))).EnsureSuccessStatusCode();
    }

    /// <summary>A raft of five lanes with an Open cycle of ten days, and seed on <paramref name="lanes"/> yesterday (on all five when none are named).</summary>
    public static async Task<SeededRaft> SeededRaftAsync(this AuthApiFactory factory, params int[] lanes)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        var session = await owner.SeedingRecordAsync(setup.CycleId, lot.Session(lanes.Length == 0 ? [1, 2, 3, 4, 5] : lanes));
        return new SeededRaft(setup.Owner, setup.ZoneId, setup.RaftId, setup.CycleId, lot, session);
    }

    /// <summary>The state of a lane on the Harvest lots screen.</summary>
    public static string HarvestStateOf(this JsonElement screen, int laneNumber) => Lane(screen, laneNumber).Text("state");

    public static bool CanHarvest(this JsonElement screen, int laneNumber) => Lane(screen, laneNumber).GetProperty("canHarvest").GetBoolean();

    public static decimal Kg(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static List<string> LotCodes(this JsonElement screen) => screen.GetProperty("lots").EnumerateArray().Select(l => l.Text("lotCode")).ToList();

    public static Task<List<Notification>> NoticesAsync(this AuthApiFactory factory, string type, Guid recordId) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.Type == type && n.RelatedRecordId == recordId).ToListAsync());

    public static Task<int> LotCountAsync(this AuthApiFactory factory, Guid raftId) =>
        factory.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raftId));

    private static JsonElement Lane(JsonElement screen, int laneNumber) =>
        screen.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber);
}
