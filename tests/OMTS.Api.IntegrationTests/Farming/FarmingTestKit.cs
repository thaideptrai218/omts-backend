using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>A logged-in client and its account.</summary>
public sealed record Actor(HttpClient Client, Account Account, AccessGrant? Grant = null);

public sealed record ZoneBody(
    string? Code, string? Name, string? Location, decimal? Latitude, decimal? Longitude, decimal? Area, string? RowVersion = null);

public sealed record RaftBody(
    string? Code, decimal? Latitude, decimal? Longitude, string? Location, string? Size, string? ConditionNotes, int? LaneCount, string? RowVersion = null,
    decimal? LengthM = null, decimal? WidthM = null, IReadOnlyList<LaneLengthBody>? LaneLengths = null);

/// <summary>The line length of one lane in a raft body (12/11/2026); a null length puts the lane back to the length of the raft.</summary>
public sealed record LaneLengthBody(int LaneNumber, decimal? LineLengthM);

/// <summary>
/// FARM-03 multipart form fields; <see cref="File"/> null sends no file. The expiry date is required since PR 0.8: it
/// defaults to five years ahead. <see cref="ZoneIds"/> sends the deprecated <c>zoneIds</c> field.
/// </summary>
public sealed record LicenseFields
{
    public string? DocumentType { get; init; } = "AQUACULTURE_LICENSE";
    public string? DocumentNumber { get; init; } = $"GP-{NewCode()}";
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? ExpiryDate { get; init; } = FarmingTestKit.Today.AddYears(5);
    public Guid? ZoneId { get; init; }
    public IReadOnlyList<Guid> ZoneIds { get; init; } = [];
    public string? IssuingAuthority { get; init; }
    public string? Holder { get; init; }
    public string? LegalScope { get; init; }
    public string? RowVersion { get; init; }
    public byte[]? File { get; init; } = FarmingTestKit.Pdf;
    public string FileName { get; init; } = "license.pdf";

    public MultipartFormDataContent ToForm()
    {
        var form = new MultipartFormDataContent();
        void Add(string? value, string name)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value), name);
            }
        }

        Add(DocumentType, "documentType");
        Add(DocumentNumber, "documentNumber");
        Add(EffectiveDate?.ToString("yyyy-MM-dd"), "effectiveDate");
        Add(ExpiryDate?.ToString("yyyy-MM-dd"), "expiryDate");
        Add(IssuingAuthority, "issuingAuthority");
        Add(Holder, "holder");
        Add(LegalScope, "legalScope");
        Add(RowVersion, "rowVersion");
        Add(ZoneId?.ToString(), "zoneId");
        foreach (var zoneId in ZoneIds)
        {
            form.Add(new StringContent(zoneId.ToString()), "zoneIds");
        }

        if (File is not null)
        {
            form.Add(FarmingTestKit.FileContent(File), "file", FileName);
        }

        return form;
    }
}

/// <summary>Shared FARM-02/03/04 helpers: accounts, request bodies, real file headers and seeded cultivation rows.</summary>
public static class FarmingTestKit
{
    public static readonly byte[] Pdf = "%PDF-1.4\n%test license\n"u8.ToArray();
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    public static readonly byte[] WebP = "RIFF\x10\0\0\0WEBPVP8 "u8.ToArray();
    public static readonly byte[] TextPretendingToBeAFile = "just some text, not a real file"u8.ToArray();

    /// <summary>Today in the business time zone (UTC+7), as the server computes it.</summary>
    public static DateOnly Today => BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

    public static ByteArrayContent FileContent(byte[] bytes, string contentType = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    /// <summary>A file of exactly <paramref name="size"/> bytes that starts with the given header.</summary>
    public static byte[] Sized(byte[] header, int size)
    {
        var bytes = new byte[size];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    public static async Task<Actor> OwnerAsync(this AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        return new Actor(await factory.LoginAsync(owner.Username), owner);
    }

    /// <summary>
    /// Logged-in staff with one Effective pair of the fixed <paramref name="role"/> in <paramref name="zone"/> (All zones
    /// when null).
    /// </summary>
    public static async Task<Actor> StaffAsync(this AuthApiFactory factory, FarmingZone? zone, StaffRole role = StaffRole.ZoneManager)
    {
        var (staff, grant) = await factory.StaffAccountAsync(zone, role);
        return new Actor(await factory.LoginAsync(staff.Username), staff, grant);
    }

    /// <summary>A staff account with one pair, not logged in (staff log in only with an Effective pair).</summary>
    public static async Task<(Account Account, AccessGrant Grant)> StaffAccountAsync(
        this AuthApiFactory factory, FarmingZone? zone, StaffRole role = StaffRole.ZoneManager, Action<AccessGrant>? configureGrant = null)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        return (staff, await factory.GrantAsync(staff, role, zone, configureGrant));
    }

    public static async Task<HttpClient> TraderAsync(this AuthApiFactory factory)
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        return await factory.LoginAsync(trader.Username);
    }

    public static Task<FarmingZone> ZoneEntityAsync(this AuthApiFactory factory, Guid zoneId) =>
        factory.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == zoneId));

    public static ZoneBody NewZoneBody(string? code = null)
    {
        code ??= NewCode();
        return new ZoneBody(code, $"Zone {code}", "Bai Tu Long bay", 21.05m, 107.35m, 12.5m);
    }

    public static RaftBody NewRaftBody(string? code = null, int lanes = 5) => new(
        code ?? NewCode(), 21.05m, 107.35m, "Raft row 2", "40 m x 20 m", null, lanes);

    /// <summary>Creates a zone and returns the saved zone, the <c>zone</c> of the answer (FARM-02.0 step 6).</summary>
    public static async Task<JsonElement> CreateZoneAsync(this HttpClient owner, ZoneBody? body = null) =>
        (await (await owner.PostAsJsonAsync("/api/zones", body ?? NewZoneBody())).ReadAsync(HttpStatusCode.Created)).GetProperty("zone");

    /// <summary>Edits a zone and returns the saved zone, the <c>zone</c> of the answer (FARM-02.1).</summary>
    public static async Task<JsonElement> EditZoneAsync(this HttpClient owner, Guid zoneId, ZoneBody body) =>
        (await (await owner.PutAsJsonAsync($"/api/zones/{zoneId}", body)).ReadAsync(HttpStatusCode.OK)).GetProperty("zone");

    /// <summary>Creates a raft and returns the saved raft, the <c>raft</c> of the answer (FARM-04.0 step 6).</summary>
    public static async Task<JsonElement> CreateRaftAsync(this HttpClient owner, Guid zoneId, RaftBody? body = null) =>
        (await (await owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", body ?? NewRaftBody())).ReadAsync(HttpStatusCode.Created)).GetProperty("raft");

    /// <summary>Edits a raft and returns the saved raft, the <c>raft</c> of the answer (FARM-04.2).</summary>
    public static async Task<JsonElement> EditRaftAsync(this HttpClient owner, Guid raftId, RaftBody body) =>
        (await (await owner.PutAsJsonAsync($"/api/rafts/{raftId}", body)).ReadAsync(HttpStatusCode.OK)).GetProperty("raft");

    /// <summary>Posts one photo; the answer is the caller's to read.</summary>
    public static Task<HttpResponseMessage> UploadRaftPhotoAsync(this HttpClient client, Guid raftId, byte[]? bytes = null, string name = "raft.png") =>
        client.PostAsync($"/api/rafts/{raftId}/photos", new MultipartFormDataContent { { FileContent(bytes ?? Png, "image/png"), "file", name } });

    /// <summary>Adds one photo and returns the saved raft.</summary>
    public static async Task<JsonElement> AddRaftPhotoAsync(this HttpClient owner, Guid raftId, byte[]? bytes = null) =>
        (await (await owner.UploadRaftPhotoAsync(raftId, bytes)).ReadAsync(HttpStatusCode.OK)).GetProperty("raft");

    /// <summary>Storage keys of the photos of a raft, in slot order, read from the database.</summary>
    public static Task<List<string>> RaftPhotoKeysAsync(this AuthApiFactory factory, Guid raftId) =>
        factory.QueryAsync(db => db.RaftPhotos.Where(p => p.RaftId == raftId).OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToListAsync());

    /// <summary>Lane numbers of a raft answer that have the status, in number order.</summary>
    public static List<int> LaneNumbers(this JsonElement raft, int statusId) => raft.GetProperty("lanes").EnumerateArray()
        .Where(l => l.GetProperty("statusId").GetInt32() == statusId).Select(l => l.GetProperty("laneNumber").GetInt32()).ToList();

    public static async Task<JsonElement> CreateLicenseAsync(this HttpClient owner, LicenseFields fields) =>
        await (await owner.PostAsync("/api/zone-licenses", fields.ToForm())).ReadAsync(HttpStatusCode.Created);

    /// <summary>Asserts the status (showing the body when it differs) and returns the JSON body.</summary>
    public static async Task<JsonElement> ReadAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The <c>fields</c> list of an MSG01 problem.</summary>
    public static async Task<List<string>> InvalidFieldsAsync(this HttpResponseMessage response)
    {
        var body = await response.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("MSG-GEN-01", body.GetProperty("code").GetString());
        return body.TryGetProperty("fields", out var fields)
            ? fields.EnumerateArray().Select(f => f.GetString()!).ToList()
            : [body.GetProperty("field").GetString()!];
    }

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static string Text(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    /// <summary>A cycle row as it stands in the database: Closed with a close date, else Open.</summary>
    public static async Task<CultivationCycle> AddCycleAsync(
        this AuthApiFactory factory, Guid raftId, DateOnly startDate, DateOnly? closeDate = null)
    {
        var cycle = new CultivationCycle
        {
            RaftId = raftId,
            StartDate = startDate,
            CloseDate = closeDate,
            StatusId = closeDate is null ? StatusIds.CultivationCycle.Open : StatusIds.CultivationCycle.Closed,
        };
        await factory.UseDbAsync(db => db.CultivationCycles.Add(cycle));
        return cycle;
    }

    /// <summary>
    /// InMemory never generates rowversions, so they stay empty – and an empty <c>rowVersion</c> form field binds as
    /// null (MSG01). Stores a version on the row, as SQL Server would, and returns it for the multipart edit forms. Its
    /// last byte is 1–254, so it never equals <see cref="AcsTestKit.StaleRowVersion"/>.
    /// </summary>
    public static async Task<string> IssueRowVersionAsync<T>(this AuthApiFactory factory, Guid id) where T : OMTS.Domain.Common.AuditableEntity
    {
        byte[] version = [0, 0, 0, 0, 0, 0, 7, (byte)Random.Shared.Next(1, 255)];
        await factory.UpdateAsync<T>(id, e => e.RowVersion = version);
        return Convert.ToBase64String(version);
    }

    /// <summary>Lane id by lane number from a raft response.</summary>
    public static Guid LaneId(this JsonElement raft, int laneNumber) =>
        raft.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber).Id();

    /// <summary>Files the Local storage provider holds under a category folder of the host.</summary>
    public static IReadOnlyList<string> StoredFiles(this AuthApiFactory factory, string category)
    {
        var folder = Path.Combine(factory.StorageRoot, category);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];
    }
}
