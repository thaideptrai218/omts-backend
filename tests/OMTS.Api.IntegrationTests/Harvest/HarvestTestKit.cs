using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The multipart fields of a recorded harvest; by default one lane, eight cages, no estimate and no photo (the kit adds one).
/// </summary>
public sealed record HarvestFields
{
    /// <summary>
    /// The <c>laneNumbers</c> sent; none by default. A lot recorded from a plan line holds the whole raft and ignores them (owner decision of 18/11/2026), a correction that sends none keeps the lanes of the lot.
    /// </summary>
    public IReadOnlyList<string>? Lanes { get; init; }
    public IReadOnlyList<string> Finished { get; init; } = [];
    public string? Cages { get; init; } = "8";
    public string? EstimatedKg { get; init; }
    public IReadOnlyList<byte[]> Photos { get; init; } = [];
    public string PhotoField { get; init; } = "photos";

    /// <summary>The line of a harvest plan the lot is recorded from; the lanes then come from the plan.</summary>
    public Guid? PlanLineId { get; init; }

    public HarvestFields FromPlanLine(Guid lineId) => this with { PlanLineId = lineId };

    /// <summary>The share of each product on the lot (product, cages, kg left empty to work it out); none keeps the old way of recording a lot.</summary>
    public IReadOnlyList<(Guid ProductId, decimal Cages, decimal? Kg)> ProductLines { get; init; } = [];

    public HarvestFields WithProducts(params (Guid ProductId, decimal Cages, decimal? Kg)[] lines) => this with { ProductLines = lines };

    /// <summary>The employees on the trip, one <c>crew</c> field each (decision of 12/11/2026); null sends none (a correction then keeps the crew); a single empty value clears it.</summary>
    public IReadOnlyList<string>? Crew { get; init; }

    /// <summary>Sends <c>replaceCrew=true</c>: the crew of a correction becomes exactly the <c>crew</c> fields, none clearing it.</summary>
    public bool ReplaceCrew { get; init; }

    /// <summary>Posts without a harvest plan line even when the kit could plan one, to see MSG-HARVEST-06.</summary>
    public bool WithoutPlan { get; init; }

    /// <summary>Sends no photo when recording, to see it refused (the kit adds one otherwise).</summary>
    public bool NoPhoto { get; init; }

    /// <summary>The <c>lanePercents</c> field (lane number to percent, owner decision of 12/11/2026); null sends none.</summary>
    public IReadOnlyDictionary<int, decimal>? LanePercents { get; init; }

    public static HarvestFields OfLanes(params int[] lanes) => new() { Lanes = lanes.Select(n => n.ToString()).ToList() };

    public HarvestFields FinishedLanes(params int[] lanes) => this with { Finished = lanes.Select(n => n.ToString()).ToList() };

    public MultipartFormDataContent ToForm()
    {
        var form = new MultipartFormDataContent();
        foreach (var lane in Lanes ?? [])
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

        for (var i = 0; i < ProductLines.Count; i++)
        {
            var (productId, cages, kg) = ProductLines[i];
            form.Add(new StringContent(productId.ToString()), $"productLines[{i}].productId");
            form.Add(new StringContent(cages.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"productLines[{i}].cageCount");
            if (kg is { } typed)
            {
                form.Add(new StringContent(typed.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"productLines[{i}].kg");
            }
        }

        if (PlanLineId is { } planLine)
        {
            form.Add(new StringContent(planLine.ToString()), "harvestPlanLineId");
        }

        if (LanePercents is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(LanePercents)), "lanePercents");
        }

        if (ReplaceCrew)
        {
            form.Add(new StringContent("true"), "replaceCrew");
        }

        foreach (var employee in Crew ?? [])
        {
            form.Add(new StringContent(employee), "crew");
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
    public static async Task<HttpResponseMessage> PostHarvestAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        (await PostAndFinishAsync(client, raftId, fields)).Answer;

    /// <summary>
    /// Posts the harvest, planned when the test names no line, and finishes the lanes the test marks finished: a lot harvests the raft and finishes no lane (owner decision of 18/11/2026), so
    /// the kit finishes them by their own action, as a client does. The answer of that action is returned with the answer of the lot.
    /// </summary>
    private static async Task<(HttpResponseMessage Answer, JsonElement? Finished)> PostAndFinishAsync(HttpClient client, Guid raftId, HarvestFields? fields)
    {
        fields ??= new HarvestFields();
        // Photos are optional (owner decision of 26/11/2026): a test that sends none gets one for convenience, unless it asks for a lot without (<see cref="HarvestFields.NoPhoto"/>).
        if (fields.Photos.Count == 0 && !fields.NoPhoto)
        {
            fields = fields with { Photos = [FarmingTestKit.Jpeg] };
        }

        // A harvest is recorded only from a line of a harvest plan (MSG-HARVEST-06): a test that names no line gets a plan of the raft.
        fields = await fields.PlannedAsync(raftId);
        var answer = await client.PostAsync($"/api/rafts/{raftId}/harvest-lots", fields.ToForm());
        if (answer.StatusCode != HttpStatusCode.Created || fields.Finished.Count == 0)
        {
            return (answer, null);
        }

        var finished = await client.FinishLanesAsync(raftId, fields.Finished.Select(int.Parse).ToArray());
        return (answer, finished);
    }

    /// <summary>
    /// The fields with the line of a new plan of the raft, for a test that builds the multipart form itself and posts it (a lot is recorded only from a plan line, MSG-HARVEST-06);
    /// they stay as they are when they already name a line or ask for none.
    /// </summary>
    public static async Task<HarvestFields> PlannedAsync(this HarvestFields fields, Guid raftId) =>
        fields.PlanLineId is null && !fields.WithoutPlan && await HarvestPlanTestKit.PlannedLineForAsync(raftId) is { } lineId ? fields.FromPlanLine(lineId) : fields;

    /// <summary>Finishes the harvest of the lanes by their own action (a lot finishes none), as a client does; the answer, which fails the test when the API refuses.</summary>
    public static async Task<JsonElement> FinishLanesAsync(this HttpClient client, Guid raftId, params int[] lanes) =>
        await (await client.PostAsJsonAsync($"/api/rafts/{raftId}/finish-harvest", new { laneNumbers = lanes })).ReadAsync(HttpStatusCode.OK);

    /// <summary>Records a harvest and returns the whole answer.</summary>
    public static async Task<JsonElement> RecordHarvestAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null)
    {
        var (answer, finished) = await PostAndFinishAsync(client, raftId, fields);
        var saved = await answer.ReadAsync(HttpStatusCode.Created);
        if (finished is not { } done)
        {
            return saved;
        }

        // The answer as the lanes finished by the test leave it: the lot as it now is (its version moved with the finish mark) and what the finish answered of the cycle.
        var node = JsonNode.Parse(saved.GetRawText())!.AsObject();
        node["lot"] = JsonNode.Parse((await client.GetHarvestLotAsync(saved.GetProperty("lot").Id())).GetRawText());
        node["everySeededLaneHarvested"] = done.GetProperty("everySeededLaneHarvested").GetBoolean();
        node["cycleRowVersion"] = done.Text("cycleRowVersion");
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    /// <summary>
    /// Records a lot and returns it as it is saved: awaiting weighing, so not for sale yet, with no kg on the ledger, its figures counting from its estimated kg and its estimate and lines still corrected by a correction.
    /// </summary>
    public static async Task<JsonElement> RecordLotAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        (await client.RecordHarvestAsync(raftId, fields)).GetProperty("lot");

    /// <summary>Records a lot and weighs it at its estimated kg, so it is Open and for sale as before weighing existed.</summary>
    public static async Task<JsonElement> HarvestLotAsync(this HttpClient client, Guid raftId, HarvestFields? fields = null) =>
        await client.WeighAtEstimateAsync((await client.RecordHarvestAsync(raftId, fields)).GetProperty("lot"));

    /// <summary>Weighs a lot awaiting weighing at the estimated kg of each product line, one cage each in line order (one cage for the lot without lines), and completes it; the weighed lot.</summary>
    public static async Task<JsonElement> WeighAtEstimateAsync(this HttpClient client, JsonElement lot)
    {
        var lines = lot.GetProperty("productLines").EnumerateArray().Select(l => l.GetProperty("kg").GetDecimal()).ToArray();
        var response = await client.PostWeighAsync(lot, lines.Length == 0 ? [lot.GetProperty("estimatedKg").GetDecimal()] : lines);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lot");
    }

    /// <summary>
    /// Weighs a lot, or weighs it again, at <paramref name="weighedKg"/> for each of its product lines in line order (for the lot itself when it has no lines): the cages on its sheet are taken off, one cage
    /// is weighed per value, then the weighing is completed with the version the lot view carries; the answer of the completion.
    /// </summary>
    public static async Task<HttpResponseMessage> PostWeighAsync(this HttpClient client, JsonElement lot, params decimal[] weighedKg)
    {
        var id = lot.Text("id")!;
        var sheet = await client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{id}/cages");
        foreach (var cage in sheet.GetProperty("cages").EnumerateArray())
        {
            (await client.DeleteAsync($"/api/harvest-lots/{id}/cages/{cage.Text("id")}")).EnsureSuccessStatusCode();
        }

        var lines = lot.GetProperty("productLines").EnumerateArray().ToList();
        for (var i = 0; i < weighedKg.Length; i++)
        {
            (await client.PostCageAsync(id, lines.Count == 0 ? null : lines[i].Text("productId"), weighedKg[i])).EnsureSuccessStatusCode();
        }

        return await client.PostAsJsonAsync($"/api/harvest-lots/{id}/weigh", new { rowVersion = lot.Text("rowVersion") });
    }

    /// <summary>Weighs one cage of a lot; the answer is the weighing sheet.</summary>
    public static Task<HttpResponseMessage> PostCageAsync(this HttpClient client, string lotId, string? productId, decimal kg, Guid? clientOperationId = null) =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/cages", new { productId, kg, clientOperationId });

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

    /// <summary>
    /// Every stored file of the category belongs to a photo row and every row has its file: a refused or failed save leaves no file behind. The lots a test recorded keep their own photo (the kit sends one
    /// when a test names none, as a new lot needs 1 to 3), so "nothing stored" reads "nothing but the photos of the lots".
    /// </summary>
    public static async Task AssertNoOrphanLotPhotosAsync(this AuthApiFactory factory, string category)
    {
        var rows = (await factory.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).ToListAsync())).Order().ToList();
        Assert.Equal(rows, FarmingUploadedFileTests.StoredKeys(factory, category).Select(k => k!).Order().ToList());
    }

    public static Task<int> LotCountAsync(this AuthApiFactory factory, Guid raftId) =>
        factory.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raftId));

    private static JsonElement Lane(JsonElement screen, int laneNumber) =>
        screen.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber);
}
