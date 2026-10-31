using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA of OMTS-217 PR 0.7, FARM-01.1 certifications: the derived state at the business midnight (BR-FARM-06, UTC+7), a
/// stale edit that brings a file, failed saves around the upload (GFR-01, nothing half-saved), an unreadable expiry date
/// and what an anonymous caller can reach of the certificate files.
/// </summary>
public class CooperativeCertificationQaTests
{
    // At 16:59:59Z and at 17:00Z the UTC date is the same day D, but the business date moves from D to D+1: a state
    // worked out on the UTC date would not change here. Expiry today, in 30 days and in 31 days on both sides.
    [Fact]
    public async Task State_turns_at_the_utc_plus_7_midnight_for_expiry_today_in_30_and_in_31_days()
    {
        using var api = new MovableClockApiFactory();
        var owner = await api.NewOwnerAsync();
        var midnight = NextBusinessMidnight(DateTimeOffset.UtcNow);
        var day = BusinessClock.ToBusinessDate(midnight.AddTicks(-1));
        Assert.Equal(day, DateOnly.FromDateTime(midnight.UtcDateTime));
        api.Clock.Set(midnight.AddTicks(-1));
        foreach (var days in new[] { 0, 30, 31, 32 })
        {
            await owner.CreateCertificationAsync(new CertificationFields { Name = $"D+{days:00}", ExpiryDate = day.AddDays(days) });
        }

        var lastDay = await StatesAsync(owner);
        api.Clock.Set(midnight);
        var nextDay = await StatesAsync(owner);

        Assert.Equal(day, lastDay.AsOf);
        Assert.Equal(["ExpiringSoon", "ExpiringSoon", "Valid", "Valid"], lastDay.States);
        Assert.Equal(["D+00", "D+30", "D+31", "D+32"], lastDay.Public);
        Assert.Equal(day.AddDays(1), nextDay.AsOf);
        Assert.Equal(["Expired", "ExpiringSoon", "ExpiringSoon", "Valid"], nextDay.States);
        Assert.Equal(["D+30", "D+31", "D+32"], nextDay.Public);
        Assert.Equal(nextDay.States, nextDay.Listed);
    }

    // A stale edit is refused before the upload: no file is written, the stored one stays. Once removed, the
    // certification is gone for an edit and for a second removal alike.
    [Fact]
    public async Task Stale_edit_with_a_new_file_uploads_nothing_and_a_removed_certification_is_404()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var id = (await owner.CreateCertificationAsync(new CertificationFields { Number = "VG-OLD" })).Id();
        var version = await api.IssueCertificationVersionAsync(id);
        var original = StoredKeys(api, FileCategories.CooperativeCertifications);

        var stale = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields
        {
            Number = "VG-NEW", RowVersion = NeverIssuedVersion, File = Png, FileName = "new.png",
        }.ToForm());

        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (stale.StatusCode, await stale.ErrorCodeAsync()));
        Assert.Equal(original, StoredKeys(api, FileCategories.CooperativeCertifications));
        Assert.Equal("VG-OLD", await api.QueryAsync(db => db.CooperativeCertifications.Select(c => c.Number).SingleAsync()));

        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}")).StatusCode);
        var editRemoved = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields { RowVersion = version }.ToForm());
        var removeAgain = await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}");

        Assert.Equal(HttpStatusCode.NotFound, editRemoved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removeAgain.StatusCode);
        Assert.Empty(StoredKeys(api, FileCategories.CooperativeCertifications));
        Assert.Equal(2, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id)));
    }

    // GFR-01 / UploadedFileSaver: a save that fails after the upload removes the upload and keeps the stored file; a
    // failed removal keeps row and file; a save committed before its reply was lost keeps the file it points to.
    [Fact]
    public async Task Failed_saves_around_the_upload_leave_no_stray_file_and_never_lose_the_stored_one()
    {
        using var api = new FileFaultApiFactory();
        var owner = await api.NewOwnerAsync();

        api.Faults.FailNextSave(DatabaseDown);
        var failedAdd = await owner.AddCertificationAsync(new CertificationFields { Number = "VG-FAILED" });
        Assert.Equal(HttpStatusCode.InternalServerError, failedAdd.StatusCode);
        Assert.Empty(StoredKeys(api, FileCategories.CooperativeCertifications));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(CooperativeCertification))));

        var id = (await owner.CreateCertificationAsync(new CertificationFields { Number = "VG-1" })).Id();
        var original = await CertificateKeyAsync(api, id);
        var version = await api.IssueCertificationVersionAsync(id);
        api.Faults.FailNextSave(Conflict);
        var conflicted = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields
        {
            Number = "VG-2", RowVersion = version, File = Png, FileName = "new.png",
        }.ToForm());
        api.Faults.FailNextSave(DatabaseDown);
        var failedRemove = await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}");

        Assert.Equal("MSG06", await conflicted.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, failedRemove.StatusCode);
        Assert.Equal(original, await CertificateKeyAsync(api, id));
        Assert.Equal([original!], StoredKeys(api, FileCategories.CooperativeCertifications));

        api.Faults.FailNextSaveAfterCommit(DatabaseDown);
        var lostReply = await owner.AddCertificationAsync(new CertificationFields { Number = "VG-LOST" });
        var lostKey = await api.QueryAsync(db => db.CooperativeCertifications.Where(c => c.Number == "VG-LOST").Select(c => c.FileUrl).SingleAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, lostReply.StatusCode);
        Assert.Equal(new[] { original!, lostKey! }.Order(), StoredKeys(api, FileCategories.CooperativeCertifications));
    }

    // A value that is not a date is MSG-GEN-01 like every invalid field, and nothing (row, file, audit) is saved.
    [Fact]
    public async Task Unreadable_expiry_date_is_msg_gen_01_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var form = new CertificationFields().ToForm();
        form.Add(new StringContent("31/02/2027"), "expiryDate");

        var response = await owner.PostAsync(Certifications, form);

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (response.StatusCode, await response.ErrorCodeAsync()));
        // Named by the form property, as every unreadable value of the API is (InvalidModelStateResponse).
        Assert.Equal("ExpiryDate", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.False(await api.QueryAsync(db => db.CooperativeCertifications.AnyAsync()));
        Assert.Empty(StoredKeys(api, FileCategories.CooperativeCertifications));
    }

    // BR-ORIGIN-01/04: a certificate file is internal. Anonymous callers get 401 on every FARM-01 endpoint, the public
    // logo and cover routes never reach a certificate file of the same shape, and the Owner's signed link stops working
    // when it is tampered with, when the file is replaced and when the certification is removed.
    [Fact]
    public async Task Certificate_files_are_reachable_only_through_the_owners_signed_link_of_the_current_file()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var certification = await owner.CreateCertificationAsync(new CertificationFields { File = Png, FileName = "cert.png" });
        var id = certification.Id();
        var key = (await CertificateKeyAsync(api, id))!;
        var path = key[(FileCategories.CooperativeCertifications.Length + 1)..];
        var link = certification.Text("fileUrl");
        var anonymous = api.CreateClient();

        Assert.Equal(Png, await anonymous.GetByteArrayAsync(link));
        foreach (var refused in new[] { Regex.Replace(link, "sig=[0-9a-f]", "sig=x"), Regex.Replace(link, @"expires=(\d+)", m => $"expires={long.Parse(m.Groups[1].Value) + 3600}") })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(refused)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(CoverPhotoFiles + path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(LogoFiles + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Profile)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Certifications)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync($"{Certifications}/{id}", new CertificationFields().ToForm())).StatusCode);
        Assert.DoesNotContain(key, await (await anonymous.GetAsync(Certifications)).Content.ReadAsStringAsync());

        var version = await api.IssueCertificationVersionAsync(id);
        var edited = await (await owner.PutAsync($"{Certifications}/{id}", new CertificationFields { RowVersion = version, File = Pdf }.ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        var newLink = edited.GetProperty("certification").Text("fileUrl");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(link)).StatusCode);
        Assert.Equal(Pdf, await anonymous.GetByteArrayAsync(newLink));

        await (await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(newLink)).StatusCode);
    }

    private static async Task<(DateOnly AsOf, List<string> States, List<string> Public, List<string> Listed)> StatesAsync(HttpClient owner)
    {
        var profile = await owner.GetFromJsonAsync<JsonElement>(Profile);
        var listed = await owner.GetFromJsonAsync<JsonElement>(Certifications);
        return (
            DateOnly.Parse(profile.Text("asOf")),
            profile.GetProperty("certifications").EnumerateArray().Select(c => c.Text("state")).ToList(),
            profile.GetProperty("publicPreview").GetProperty("certifications").EnumerateArray().Select(c => c.Text("name")).ToList(),
            listed.EnumerateArray().Select(c => c.Text("state")).ToList());
    }
}
