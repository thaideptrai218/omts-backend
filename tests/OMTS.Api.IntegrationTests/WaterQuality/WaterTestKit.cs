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

/// <summary>A zone with thresholds pH 6.5–8.5 and salinity 15–30 ‰, its Owner and helpers to record measurements.</summary>
public sealed record WaterZone(AuthApiFactory Api, FarmingZone Zone, HttpClient Owner, Account OwnerAccount)
{
    public string Base => $"/api/zones/{Zone.Id}/water-measurements";
}

public static class WaterTestKit
{
    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];

    public static async Task<WaterZone> ZoneAsync(this AuthApiFactory api, bool thresholds = true)
    {
        var zone = await api.AddZoneAsync(NewCode());
        await api.UpdateAsync<FarmingZone>(zone.Id, z =>
        {
            z.WaterMeasurementPoint = "Cọc đo số 1";
            if (thresholds)
            {
                (z.PhMin, z.PhMax, z.SalinityMin, z.SalinityMax) = (6.5m, 8.5m, 15m, 30m);
            }
        });
        var owner = await api.AddAccountAsync(NewCode(), Password);
        return new WaterZone(api, zone, await api.LoginAsync(owner.Username), owner);
    }

    /// <summary>A Zone Manager whose role holds the given actions on WATER_MEASUREMENTS, granted in <paramref name="zone"/>.</summary>
    public static async Task<(HttpClient Client, Account Account)> StaffAsync(
        this AuthApiFactory api, FarmingZone? zone, bool create = false, bool edit = false, bool export = false, bool view = true)
    {
        var role = new Role { Name = $"Water {NewCode()}" };
        role.Permissions.Add(new RolePermission
        {
            FunctionCode = PermissionFunctions.WaterMeasurements,
            CanView = view,
            CanCreate = create,
            CanEdit = edit,
            OtherPermissions = export ? ["EXPORT"] : [],
        });
        await api.UseDbAsync(db => db.Roles.Add(role));
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        if (zone is not null)
        {
            await api.GrantAsync(account, role, zone);
        }

        return (await api.LoginAsync(account.Username), account);
    }

    /// <summary>A PNG whose bytes carry the fake OCR markers (e.g. "ph9.1_sal20").</summary>
    public static byte[] Photo(string marker = "") => [.. PngSignature, .. Encoding.ASCII.GetBytes(marker)];

    public static MultipartFormDataContent PhotoForm(byte[] photo, Guid? clientOperationId = null, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "photo", "meter.png");
        if (clientOperationId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "clientOperationId");
        }

        return form;
    }

    public static Task<HttpResponseMessage> RecognizeAsync(this WaterZone z, string marker = "", Guid? clientOperationId = null, HttpClient? client = null) =>
        (client ?? z.Owner).PostAsync($"{z.Base}/recognize", PhotoForm(Photo(marker), clientOperationId));

    public static Task<HttpResponseMessage> RecordAsync(
        this WaterZone z, decimal? ph, decimal? salinity, bool confirm = true, Guid? clientOperationId = null, DateTimeOffset? measuredAt = null,
        HttpClient? client = null) =>
        (client ?? z.Owner).PostAsJsonAsync(z.Base, new
        {
            clientOperationId = clientOperationId ?? Guid.NewGuid(),
            measuredAt = measuredAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            ph,
            salinity,
            confirm,
        });

    /// <summary>Records a measurement and returns its body; fails the test unless it was created.</summary>
    public static async Task<JsonElement> RecordedAsync(
        this WaterZone z, decimal? ph, decimal? salinity, bool confirm = true, DateTimeOffset? measuredAt = null, HttpClient? client = null)
    {
        var response = await z.RecordAsync(ph, salinity, confirm, measuredAt: measuredAt, client: client);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static Task<HttpResponseMessage> CorrectAsync(
        this HttpClient client, Guid id, decimal ph, decimal salinity, int recordVersion, string? reason = "Đọc nhầm số", DateTimeOffset? measuredAt = null) =>
        client.PostAsJsonAsync($"/api/water-measurements/{id}/corrections", new
        {
            ph,
            salinity,
            measuredAt = measuredAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            reason,
            recordVersion,
        });

    public static Task<HttpResponseMessage> VoidAsync(this HttpClient client, Guid id, int recordVersion, string? reason = "Ghi nhầm vùng") =>
        client.PostAsJsonAsync($"/api/water-measurements/{id}/void", new { reason, recordVersion });

    public static Guid Id(this JsonElement body) => body.GetProperty("id").GetGuid();

    public static string? Text(this JsonElement body, string property) => body.GetProperty(property).GetString();

    /// <summary>WATER_ALERT rows of one measurement.</summary>
    public static Task<List<Notification>> AlertsOfAsync(this AuthApiFactory api, Guid measurementId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterAlert && n.RelatedRecordCode == measurementId.ToString())
            .ToListAsync());

    public static Task<List<AuditEvent>> AuditOfAsync(this AuthApiFactory api, Guid recordId) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).ToListAsync());
}
