using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>A corrected lot as sent: the fields of a harvest, the photos to keep, the reason and the version.</summary>
public sealed record HarvestCorrection(HarvestFields Fields, string? Reason = "Wrong count at sea", string? RowVersion = null, IReadOnlyList<string>? Keep = null)
{
    public MultipartFormDataContent ToForm()
    {
        var form = Fields.ToForm();
        foreach (var key in Keep ?? [])
        {
            form.Add(new StringContent(key), "keepPhotoKeys");
        }

        if (Reason is not null)
        {
            form.Add(new StringContent(Reason), "reason");
        }

        if (RowVersion is not null)
        {
            form.Add(new StringContent(RowVersion), "rowVersion");
        }

        return form;
    }
}

/// <summary>The sales side a test sets without making invoices: which lots are on an invoice and what is sold of them (the real port is HarvestLotSalesQuery since PR 6a).</summary>
public sealed class SalesDouble : IHarvestLotSalesQuery
{
    public HashSet<Guid> OnInvoice { get; } = [];

    public Dictionary<Guid, LotSalesFigures> Figures { get; } = [];

    public Task<IReadOnlyDictionary<Guid, LotSalesFigures>> FiguresAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, LotSalesFigures>>(lotIds.Distinct().ToDictionary(id => id, id => Figures.GetValueOrDefault(id, LotSalesFigures.None)));

    public Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult(OnInvoice.Contains(lotId));

    public Task<IReadOnlySet<Guid>> LotsOnAnyInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(lotIds.Where(OnInvoice.Contains).ToHashSet());

    // The double has no cancelled invoice: a lot on an invoice is on a current one.
    public Task<IReadOnlySet<Guid>> LotsOnCurrentInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) => LotsOnAnyInvoiceAsync(lotIds, cancellationToken);
}

/// <summary>The API whose sales port is <see cref="SalesDouble"/>.</summary>
public sealed class SalesDoubleApiFactory : AuthApiFactory
{
    public SalesDouble Sales { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHarvestLotSalesQuery>();
            services.AddSingleton<IHarvestLotSalesQuery>(Sales);
        });
    }
}

/// <summary>HARVEST-01.1 calls.</summary>
public static class HarvestLotCorrectionTestKit
{
    /// <summary>
    /// Sends the correction. One that names no lanes keeps the lanes of the lot (a lot holds the whole raft since 18/11/2026, so a test rarely names them): the lanes are read from the lot as this caller sees it.
    /// </summary>
    public static async Task<HttpResponseMessage> PutHarvestLotAsync(this HttpClient client, Guid lotId, HarvestCorrection correction)
    {
        if (correction.Fields.Lanes is null && await client.GetAsync($"/api/harvest-lots/{lotId}") is { IsSuccessStatusCode: true } read)
        {
            var lanes = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("laneNumbers").EnumerateArray().Select(n => n.GetInt32().ToString()).ToList();
            correction = correction with { Fields = correction.Fields with { Lanes = lanes } };
        }

        return await client.PutAsync($"/api/harvest-lots/{lotId}", correction.ToForm());
    }

    public static Task<HttpResponseMessage> PostRemoveLotAsync(this HttpClient client, Guid lotId, string? reason, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/remove", new { reason, rowVersion });

    /// <summary>A version for the forms: the in-memory database issues none.</summary>
    public static Task<string> LotVersionAsync(this AuthApiFactory factory, Guid lotId) => factory.IssueRowVersionAsync<HarvestLot>(lotId);

    /// <summary>Sends a correction with the current version, keeping the photos named.</summary>
    public static async Task<HttpResponseMessage> SendCorrectionAsync(
        this AuthApiFactory factory, HttpClient client, Guid lotId, HarvestFields fields, string? reason = "Wrong count at sea", IReadOnlyList<string>? keep = null)
    {
        // A lot that has photos keeps 1 to 3 (owner decision of 12/11/2026), and every lot a test records has one: a correction that names no photo keeps what the lot has.
        keep ??= await KeysOfAsync(client, lotId);
        return await client.PutHarvestLotAsync(lotId, new HarvestCorrection(fields, reason, await factory.LotVersionAsync(lotId), keep));
    }

    /// <summary>A correction that must be saved: the whole answer.</summary>
    public static async Task<JsonElement> CorrectLotAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, HarvestFields fields, IReadOnlyList<string>? keep = null) =>
        await (await factory.SendCorrectionAsync(client, lotId, fields, keep: keep)).ReadAsync(HttpStatusCode.OK);

    /// <summary>
    /// Records a lot of only the lanes <paramref name="fields"/> names, finished ones included, for a test that needs lanes in different lots. A lot recorded from a plan line holds the whole raft
    /// (owner decision of 18/11/2026), so the kit records it and then takes the other lanes out by a correction of the lanes, which a client can still do; the lot is left awaiting weighing.
    /// </summary>
    public static async Task<JsonElement> RecordLotOfLanesAsync(this AuthApiFactory factory, HttpClient client, Guid raftId, HarvestFields fields)
    {
        var recorded = (await client.RecordHarvestAsync(raftId, fields with { Lanes = null, Finished = [] })).GetProperty("lot");
        return (await factory.CorrectLotAsync(client, recorded.Id(), fields)).GetProperty("lot");
    }

    /// <summary>The lot of <see cref="RecordLotOfLanesAsync"/> weighed at its estimate, so it is Open and for sale.</summary>
    public static async Task<JsonElement> HarvestLotOfLanesAsync(this AuthApiFactory factory, HttpClient client, Guid raftId, HarvestFields fields) =>
        await client.WeighAtEstimateAsync(await factory.RecordLotOfLanesAsync(client, raftId, fields));

    /// <summary>
    /// <see cref="RecordLotOfLanesAsync(AuthApiFactory, HttpClient, Guid, HarvestFields)"/> for a host with a real database (SQL Server), where the lot view carries its own version: the version
    /// the correction needs is read from it. A correction keeps the photos the lot has.
    /// </summary>
    public static async Task<JsonElement> RecordLotOfLanesAsync(this HttpClient client, Guid raftId, HarvestFields fields)
    {
        var recorded = (await client.RecordHarvestAsync(raftId, fields with { Lanes = null, Finished = [] })).GetProperty("lot");
        var correction = new HarvestCorrection(fields, "Wrong count at sea", recorded.Text("rowVersion"), recorded.PhotoKeysOfLot());
        return (await (await client.PutHarvestLotAsync(recorded.Id(), correction)).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");
    }

    /// <summary>The lot of <see cref="RecordLotOfLanesAsync(HttpClient, Guid, HarvestFields)"/> weighed at its estimate, so it is Open and for sale.</summary>
    public static async Task<JsonElement> HarvestLotOfLanesAsync(this HttpClient client, Guid raftId, HarvestFields fields) =>
        await client.WeighAtEstimateAsync(await client.RecordLotOfLanesAsync(raftId, fields));

    public static async Task<HttpResponseMessage> SendRemovalAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, string? reason = "Recorded by mistake") =>
        await client.PostRemoveLotAsync(lotId, reason, await factory.LotVersionAsync(lotId));

    /// <summary>A removal that must be saved.</summary>
    public static async Task RemoveLotAsync(this AuthApiFactory factory, HttpClient client, Guid lotId) =>
        Assert.Equal(HttpStatusCode.NoContent, (await factory.SendRemovalAsync(client, lotId)).StatusCode);

    /// <summary>The photo keys of the lot as this caller reads it; none for a caller who may not read it (its correction is refused anyway).</summary>
    public static async Task<IReadOnlyList<string>> KeysOfAsync(HttpClient client, Guid lotId)
    {
        var answer = await client.GetAsync($"/api/harvest-lots/{lotId}");
        return answer.IsSuccessStatusCode ? JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.PhotoKeysOfLot() : [];
    }

    public static List<string> PhotoKeysOfLot(this JsonElement lot) => lot.GetProperty("photos").EnumerateArray().Select(p => p.Text("key")).ToList();

    public static Task<JsonElement> LotsForSaleAsync(this HttpClient client, Guid? zoneId = null) =>
        client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/for-sale{(zoneId is null ? string.Empty : $"?zoneId={zoneId}")}");

    public static List<string> ForSaleCodes(this JsonElement list) => list.GetProperty("lots").EnumerateArray().Select(l => l.Text("lotCode")).ToList();

    public static async Task<int> ForSaleCountAsync(this HttpClient client, Guid? zoneId = null) =>
        (await client.LotsForSaleAsync(zoneId)).GetProperty("lots").GetArrayLength();

    /// <summary>Zone KV01, a raft and seed on every lane by the clock's first day of October: lot codes and dates are exact, and a run may cross midnight.</summary>
    public static async Task<(HttpClient Owner, Guid ZoneId, Guid RaftId, Guid CycleId)> ClockedRaftAsync(this CyclesQaClockApiFactory api, string raftCode = "KV01-B01", string zoneCode = "KV01")
    {
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync(FarmingTestKit.NewZoneBody(zoneCode))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, FarmingTestKit.NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        var lot = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, lot.Session([1, 2, 3, 4, 5], new DateOnly(2026, 10, 1)));
        return (owner, zoneId, raftId, cycleId);
    }

    public static DateTimeOffset InVietnam(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(7));
}
