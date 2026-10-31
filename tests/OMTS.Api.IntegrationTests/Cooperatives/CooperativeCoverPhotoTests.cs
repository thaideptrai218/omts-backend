using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01 cover photo: uploaded like the logo (PNG, JPEG or WebP, 5 MB), served anonymously for the provenance page,
/// but only the current file is kept (its own category, covered by the orphan file clean-up). Upload failure: MSG-GEN-03
/// and nothing else changes (GFR-01).
/// </summary>
public class CooperativeCoverPhotoTests
{
    private const string CoverPhoto = "/api/cooperative/profile/cover-photo";
    private const int FiveMegabytes = 5 * 1024 * 1024;

    [Fact]
    public async Task Upload_serves_the_cover_publicly_replace_and_remove_delete_the_old_file_and_are_audited()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        var owner = await api.NewOwnerAsync();
        var anonymous = api.CreateClient();

        var uploaded = await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        var first = uploaded.GetProperty("profile");
        var firstUrl = first.Text("coverPhotoUrl");
        var firstKey = await CoverKeyAsync(api);
        var served = await anonymous.GetAsync(firstUrl);

        Assert.Equal(("Cover photo saved.", "MSG-GEN-05"), (uploaded.Text("message"), uploaded.Text("messageCode")));
        Assert.StartsWith("/api/public/cooperative/cover-photos/", firstUrl);
        Assert.Equal(firstUrl, first.GetProperty("publicPreview").Text("coverPhotoUrl"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["nosniff"], served.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());

        var second = (await (await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg")).ReadAsync(HttpStatusCode.OK)).GetProperty("profile");
        var secondUrl = second.Text("coverPhotoUrl");
        var secondKey = await CoverKeyAsync(api);

        Assert.NotEqual(firstUrl, secondUrl);
        Assert.EndsWith(".jpg", secondKey);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(firstUrl)).StatusCode);
        Assert.Equal(Jpeg, await anonymous.GetByteArrayAsync(secondUrl));
        Assert.Equal([Path.Combine(api.StorageRoot, secondKey!.Replace('/', Path.DirectorySeparatorChar))], api.StoredFiles(FileCategories.CooperativeCoverPhotos));

        var removedAnswer = await (await owner.DeleteAsync(CoverPhoto)).ReadAsync(HttpStatusCode.OK);
        var removed = removedAnswer.GetProperty("profile");
        var removedAgain = await (await owner.DeleteAsync(CoverPhoto)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Cooperative profile saved.", "MSG-GEN-05"), (removedAnswer.Text("message"), removedAnswer.Text("messageCode")));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("coverPhotoUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("publicPreview").GetProperty("coverPhotoUrl").ValueKind);
        Assert.Equal("MSG-GEN-05", removedAgain.Text("messageCode"));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(secondUrl)).StatusCode);
        Assert.Empty(api.StoredFiles(FileCategories.CooperativeCoverPhotos));

        var changes = await api.QueryAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordId == cooperative.Id && e.Action == "Cooperative.CoverPhotoChanged").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(3, changes.Count);
        Assert.Equal((null, firstKey), (Key(changes[0].Before), Key(changes[0].After)));
        Assert.Equal((firstKey, secondKey), (Key(changes[1].Before), Key(changes[1].After)));
        Assert.Equal((secondKey, null), (Key(changes[2].Before), Key(changes[2].After)));
    }

    [Fact]
    public async Task Pdf_gif_empty_oversized_and_missing_files_are_refused_and_the_cover_stays()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        var key = await CoverKeyAsync(api);

        var refused = new[]
        {
            await owner.UploadCoverPhotoAsync(Pdf),
            await owner.UploadCoverPhotoAsync(CooperativeProfileRequests.Gif),
            await owner.UploadCoverPhotoAsync([]),
            await owner.UploadCoverPhotoAsync(Sized(Png, FiveMegabytes + 1)),
            await owner.UploadCoverPhotoAsync(Png, field: "image"),
        };
        var webpAtTheLimit = await owner.UploadCoverPhotoAsync(Sized(WebP, FiveMegabytes), "cover.webp");

        foreach (var response in refused)
        {
            Assert.Equal(["Cover photo"], await response.InvalidFieldsAsync());
            Assert.Equal("Cover photo is missing or invalid. Please check and enter it again.", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        }

        Assert.Equal(HttpStatusCode.OK, webpAtTheLimit.StatusCode);
        Assert.NotEqual(key, await CoverKeyAsync(api));
        Assert.EndsWith(".webp", await CoverKeyAsync(api));
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.CoverPhotoChanged")));
    }

    // GFR-01: MSG-GEN-03 (old code MSG05); the current cover photo and the other profile entries are kept.
    [Fact]
    public async Task Storage_failure_answers_502_msg_gen_03_and_keeps_the_cover_and_the_profile()
    {
        using var api = new AuthApiFactory();
        var cooperative = await api.EnsureCooperativeAsync();
        const string oldKey = "cooperative-covers/2026/10/0123456789abcdef0123456789abcdef.png";
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => (c.CoverPhotoUrl, c.ShortName) = (oldKey, "HTX Trung Nam"));
        var owner = await api.NewOwnerAsync();
        // A file where the storage folder should be: the Local provider cannot create its folders.
        await File.WriteAllTextAsync(api.StorageRoot, "not a folder");
        try
        {
            var problem = await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.BadGateway);

            Assert.Equal(("MSG-GEN-03", "MSG05"), (problem.Text("code"), problem.Text("legacyCode")));
            Assert.Equal("The file could not be uploaded. Your other entries are kept; please try again.", problem.Text("detail"));
            var stored = await api.QueryAsync(db => db.Cooperatives.AsNoTracking().SingleAsync());
            Assert.Equal((oldKey, "HTX Trung Nam"), (stored.CoverPhotoUrl, stored.ShortName));
            Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(Cooperative))));
        }
        finally
        {
            File.Delete(api.StorageRoot);
        }
    }

    // Each public route reaches its own current files only: never a logo through the cover route, a cover through the
    // logo route, or a cover file the profile no longer points to.
    [Fact]
    public async Task Cover_route_serves_only_the_current_cover_and_the_logo_route_never_a_cover()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var logoUrl = (await (await CooperativeProfileRequests.UploadLogoAsync(owner, Png, "logo.png")).ReadAsync(HttpStatusCode.OK)).Text("logoUrl");
        var coverUrl = (await (await owner.UploadCoverPhotoAsync(Jpeg, "cover.jpg")).ReadAsync(HttpStatusCode.OK)).GetProperty("profile").Text("coverPhotoUrl");
        var orphan = await api.Services.GetRequiredService<IFileStorage>().UploadAsync(Validated(Png), FileCategories.CooperativeCoverPhotos);
        var anonymous = api.CreateClient();
        var coverPath = coverUrl["/api/public/cooperative/cover-photos/".Length..];
        var logoPath = logoUrl["/api/public/cooperative/logos/".Length..];

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(coverUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/public/cooperative/logos/{coverPath}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/public/cooperative/cover-photos/{logoPath}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/public/cooperative/cover-photos/{orphan.Key!["cooperative-covers/".Length..]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/public/cooperative/cover-photos/..%2F..%2Fcooperative-logos%2F{logoPath.Replace("/", "%2F")}")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_uploads_or_removes_the_cover_photo()
    {
        using var api = new AuthApiFactory();
        await api.EnsureCooperativeAsync();
        var others = new[]
        {
            await api.NonOwnerAsync(SubjectType.Staff, StaffRole.ZoneManager),
            await api.NonOwnerAsync(SubjectType.Staff, StaffRole.Accountant),
            await api.NonOwnerAsync(SubjectType.Trader),
        };

        foreach (var client in others)
        {
            Assert.Equal("MSG-GEN-08", await (await client.UploadCoverPhotoAsync(Png)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.DeleteAsync(CoverPhoto)).ErrorCodeAsync());
        }

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.UploadCoverPhotoAsync(Png)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(CoverPhoto)).StatusCode);
        Assert.Null(await CoverKeyAsync(api));
        Assert.Empty(api.StoredFiles(FileCategories.CooperativeCoverPhotos));
    }

    // The orphan file clean-up covers both new categories: old files no record points to are deleted, the current
    // cover photo, the current certificate files and the files written within the grace period are kept.
    [Fact]
    public async Task Orphan_clean_up_deletes_unreferenced_cover_photos_and_certificate_files_only()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        await (await owner.UploadCoverPhotoAsync(Png)).ReadAsync(HttpStatusCode.OK);
        await owner.CreateCertificationAsync();
        var storage = api.Services.GetRequiredService<IFileStorage>();
        var orphanCover = (await storage.UploadAsync(Validated(Png), FileCategories.CooperativeCoverPhotos)).Key!;
        var orphanCertificate = (await storage.UploadAsync(Validated(Pdf), FileCategories.CooperativeCertifications)).Key!;
        var aged = AllKeys(api);
        foreach (var key in aged)
        {
            File.SetLastWriteTimeUtc(Path.Combine(api.StorageRoot, key), DateTime.UtcNow.AddDays(-2));
        }

        var fresh = (await storage.UploadAsync(Validated(Pdf), FileCategories.CooperativeCertifications)).Key!;

        Assert.Equal(2, await api.Services.GetRequiredService<OrphanFileCleanupJob>().RunOnceAsync(DateTimeOffset.UtcNow));

        Assert.Equal(aged.Except([orphanCover, orphanCertificate]).Append(fresh).Order(), AllKeys(api).Order());
        Assert.Equal(await CoverKeyAsync(api), Assert.Single(aged, k => k.StartsWith("cooperative-covers/") && k != orphanCover));
    }

    private static Task<string?> CoverKeyAsync(AuthApiFactory api) => api.QueryAsync(db => db.Cooperatives.Select(c => c.CoverPhotoUrl).SingleAsync());

    private static string? Key(string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.GetProperty("CoverPhotoUrl").GetString();

    private static ValidatedFile Validated(byte[] content)
    {
        var result = FileValidator.Validate([new IncomingFile("file", content.Length, new MemoryStream(content))], FileRules.Documents);
        Assert.True(result.IsValid);
        return result.Files[0];
    }

    /// <summary>Every file under the host's storage folder, as a storage key.</summary>
    private static List<string> AllKeys(AuthApiFactory api) => Directory.GetFiles(api.StorageRoot, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(api.StorageRoot, f).Replace('\\', '/')).ToList();
}
