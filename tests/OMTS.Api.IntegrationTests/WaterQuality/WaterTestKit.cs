using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>The real API on EF InMemory with extra settings (e.g. a short OCR timeout) and service overrides.</summary>
public sealed class WaterApiFactory(IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? services = null)
    : AuthApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }
}

/// <summary>A zone with thresholds pH 6.5–8.5, salinity 15–30 ‰ and temperature 10–35 °C, a raft In Use, its Owner and helpers to record measurements.</summary>
public sealed record WaterZone(AuthApiFactory Api, FarmingZone Zone, Raft Raft, HttpClient Owner, Account OwnerAccount)
{
    public string Base => $"/api/zones/{Zone.Id}/water-measurements";
}

public static class WaterTestKit
{
    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    public static async Task<WaterZone> ZoneAsync(this AuthApiFactory api, bool thresholds = true)
    {
        var zone = await api.AddZoneAsync(NewCode());
        if (thresholds)
        {
            await api.UpdateAsync<FarmingZone>(zone.Id, z =>
            {
                (z.PhMin, z.PhMax, z.SalinityMin, z.SalinityMax) = (6.5m, 8.5m, 15m, 30m);
                (z.TemperatureMin, z.TemperatureMax) = (10m, 35m);
            });
        }

        var raft = await api.RaftAsync(zone);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        return new WaterZone(api, zone, raft, await api.LoginAsync(owner.Username), owner);
    }

    public static async Task<Raft> RaftAsync(this AuthApiFactory api, FarmingZone zone, string? code = null)
    {
        var raft = new Raft { ZoneId = zone.Id, Code = code ?? $"B-{NewCode()}" };
        await api.UseDbAsync(db => db.Rafts.Add(raft));
        return raft;
    }

    /// <summary>
    /// Staff with one pair of the fixed <paramref name="role"/> in <paramref name="zone"/>: a Zone Manager holds every
    /// WATER_MEASUREMENTS action (WATER-01…03), an Accountant none (§I.4.4).
    /// </summary>
    public static async Task<(HttpClient Client, Account Account)> StaffAsync(
        this AuthApiFactory api, FarmingZone zone, StaffRole role = StaffRole.ZoneManager)
    {
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(account, role, zone);
        return (await api.LoginAsync(account.Username), account);
    }

    /// <summary>A PNG whose bytes carry the fake OCR markers (e.g. "ph9.1_sal20_tmp28").</summary>
    public static byte[] Photo(string marker = "") => [.. PngSignature, .. Encoding.ASCII.GetBytes(marker)];

    public static MultipartFormDataContent PhotoForm(byte[] photo, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "photo", "meter.png");
        return form;
    }

    public static Task<HttpResponseMessage> RecognizeAsync(this WaterZone z, string marker = "", HttpClient? client = null) =>
        (client ?? z.Owner).RecognizeAsync(z.Zone.Id, marker);

    public static Task<HttpResponseMessage> RecognizeAsync(this HttpClient client, Guid zoneId, string marker = "") =>
        client.PostAsync($"/api/zones/{zoneId}/water-measurements/recognize", PhotoForm(Photo(marker)));

    /// <summary>The key of a new meter photo of this caller, as recognize answers it; empty when the caller is refused.</summary>
    public static async Task<string> PhotoKeyAsync(this HttpClient client, Guid zoneId, string marker = "")
    {
        var response = await client.RecognizeAsync(zoneId, marker);
        return response.StatusCode == HttpStatusCode.OK
            ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photoKey").GetString()!
            : string.Empty;
    }

    /// <summary>Recognize, then the record request with the values the user confirmed.</summary>
    public static async Task<HttpResponseMessage> RecordAsync(
        this WaterZone z, decimal? ph, decimal? salinity, decimal? temperature = 25m, Guid? clientOperationId = null, DateTimeOffset? measuredAt = null,
        HttpClient? client = null, Guid? raftId = null, string? photoKey = null)
    {
        var caller = client ?? z.Owner;
        return await caller.PostAsJsonAsync(z.Base, new
        {
            clientOperationId,
            raftId = raftId ?? z.Raft.Id,
            measuredAt = measuredAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            ph,
            salinity,
            temperature,
            photoKey = photoKey ?? await caller.PhotoKeyAsync(z.Zone.Id),
        });
    }

    /// <summary>The record request with a time given as text, as a client sends it.</summary>
    public static async Task<HttpResponseMessage> RecordAtAsync(this WaterZone z, string measuredAt) =>
        await z.Owner.PostAsJsonAsync(z.Base, new
        {
            raftId = z.Raft.Id,
            measuredAt,
            ph = 7.2m,
            salinity = 20m,
            temperature = 25m,
            photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id),
        });

    /// <summary>Records a measurement and returns its body; fails the test unless it was created.</summary>
    public static async Task<JsonElement> RecordedAsync(
        this WaterZone z, decimal ph, decimal salinity, DateTimeOffset? measuredAt = null, HttpClient? client = null, decimal temperature = 25m)
    {
        var response = await z.RecordAsync(ph, salinity, temperature, measuredAt: measuredAt, client: client);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The correction request; without <paramref name="raftId"/> it names the raft the measurement has, as the caller can read it.</summary>
    public static async Task<HttpResponseMessage> CorrectAsync(
        this HttpClient client, Guid id, decimal ph, decimal salinity, string? reason = "Đọc nhầm số", DateTimeOffset? measuredAt = null, decimal? temperature = 25m, Guid? raftId = null) =>
        await client.PostAsJsonAsync($"/api/water-measurements/{id}/corrections", new
        {
            raftId = raftId ?? await CurrentRaftAsync(client, id),
            ph,
            salinity,
            temperature,
            measuredAt = measuredAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            reason,
        });

    private static async Task<Guid?> CurrentRaftAsync(HttpClient client, Guid id)
    {
        var detail = await client.GetAsync($"/api/water-measurements/{id}");
        return detail.IsSuccessStatusCode ? (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("raftId").GetGuid() : null;
    }

    public static Task<HttpResponseMessage> VoidAsync(this HttpClient client, Guid id, string? reason = "Ghi nhầm vùng") =>
        client.PostAsJsonAsync($"/api/water-measurements/{id}/void", new { reason });

    public static Guid Id(this JsonElement body) => body.GetProperty("id").GetGuid();

    public static string? Text(this JsonElement body, string property) => body.GetProperty(property).GetString();

    /// <summary>WATER_ALERT rows of one measurement.</summary>
    public static Task<List<Notification>> AlertsOfAsync(this AuthApiFactory api, Guid measurementId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterAlert && n.RelatedRecordId == measurementId)
            .ToListAsync());

    /// <summary>WATER_ALERT_WITHDRAWN rows (MSG-WATER-04) of one measurement.</summary>
    public static Task<List<Notification>> WithdrawalsOfAsync(this AuthApiFactory api, Guid measurementId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterAlertWithdrawn && n.RelatedRecordId == measurementId)
            .ToListAsync());

    public static Task<List<AuditEvent>> AuditOfAsync(this AuthApiFactory api, Guid recordId) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).ToListAsync());
}
