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
    public static Task<HttpResponseMessage> PutHarvestLotAsync(this HttpClient client, Guid lotId, HarvestCorrection correction) =>
        client.PutAsync($"/api/harvest-lots/{lotId}", correction.ToForm());

    public static Task<HttpResponseMessage> PostRemoveLotAsync(this HttpClient client, Guid lotId, string? reason, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/remove", new { reason, rowVersion });

    /// <summary>A version for the forms: the in-memory database issues none.</summary>
    public static Task<string> LotVersionAsync(this AuthApiFactory factory, Guid lotId) => factory.IssueRowVersionAsync<HarvestLot>(lotId);

    /// <summary>Sends a correction with the current version, keeping the photos named.</summary>
    public static async Task<HttpResponseMessage> SendCorrectionAsync(
        this AuthApiFactory factory, HttpClient client, Guid lotId, HarvestFields fields, string? reason = "Wrong count at sea", IReadOnlyList<string>? keep = null) =>
        await client.PutHarvestLotAsync(lotId, new HarvestCorrection(fields, reason, await factory.LotVersionAsync(lotId), keep));

    /// <summary>A correction that must be saved: the whole answer.</summary>
    public static async Task<JsonElement> CorrectLotAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, HarvestFields fields, IReadOnlyList<string>? keep = null) =>
        await (await factory.SendCorrectionAsync(client, lotId, fields, keep: keep)).ReadAsync(HttpStatusCode.OK);

    public static async Task<HttpResponseMessage> SendRemovalAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, string? reason = "Recorded by mistake") =>
        await client.PostRemoveLotAsync(lotId, reason, await factory.LotVersionAsync(lotId));

    /// <summary>A removal that must be saved.</summary>
    public static async Task RemoveLotAsync(this AuthApiFactory factory, HttpClient client, Guid lotId) =>
        Assert.Equal(HttpStatusCode.NoContent, (await factory.SendRemovalAsync(client, lotId)).StatusCode);

    public static List<string> PhotoKeysOfLot(this JsonElement lot) => lot.GetProperty("photos").EnumerateArray().Select(p => p.Text("key")).ToList();

    public static Task<JsonElement> LotsForSaleAsync(this HttpClient client, Guid? zoneId = null) =>
        client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/for-sale{(zoneId is null ? string.Empty : $"?zoneId={zoneId}")}");

    public static List<string> ForSaleCodes(this JsonElement list) => list.GetProperty("lots").EnumerateArray().Select(l => l.Text("lotCode")).ToList();

    public static async Task<int> ForSaleCountAsync(this HttpClient client, Guid? zoneId = null) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/for-sale/count{(zoneId is null ? string.Empty : $"?zoneId={zoneId}")}")).GetProperty("count").GetInt32();

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
