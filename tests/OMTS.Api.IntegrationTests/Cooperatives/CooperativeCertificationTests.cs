using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// FARM-01.1 certifications: add, list, edit, remove (Owner only, audited), the derived state at the 30-day boundary
/// (BR-FARM-06), the file rules and the upload failure (GFR-01). Each test gets its own database.
/// </summary>
public class CooperativeCertificationTests
{
    [Fact]
    public async Task Add_list_edit_and_remove_a_certification_with_its_file_and_audit()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();

        var added = await (await owner.AddCertificationAsync(new CertificationFields
        {
            Name = " Chứng nhận VietGAP ", Number = "VG-01", Issuer = "Quacert", ExpiryDate = Today.AddDays(400), File = Png, FileName = "vietgap.png",
        })).ReadAsync(HttpStatusCode.Created);
        var certification = added.GetProperty("certification");
        var id = certification.Id();
        var firstFile = await FileKeyAsync(api, id);

        Assert.Equal(("Certification saved.", "MSG-GEN-05"), (added.Text("message"), added.Text("messageCode")));
        Assert.Equal(("Chứng nhận VietGAP", "VG-01", "Quacert", "Valid"), (certification.Text("name"), certification.Text("number"), certification.Text("issuer"), certification.Text("state")));
        Assert.Equal(Png, await api.CreateClient().GetByteArrayAsync(certification.Text("fileUrl")));
        Assert.StartsWith($"{FileCategories.CooperativeCertifications}/", firstFile);

        var listed = await (await owner.GetAsync(Certifications)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([id], listed.EnumerateArray().Select(c => c.Id()));

        // Edit with a new file: the old file is deleted once the edit is saved; only the changed fields are audited.
        var version = await api.IssueCertificationVersionAsync(id);
        var edited = await (await owner.PutAsync($"{Certifications}/{id}", new CertificationFields
        {
            Name = "Chứng nhận VietGAP", Number = "VG-02", Issuer = "Quacert", ExpiryDate = Today.AddDays(400), RowVersion = version,
        }.ToForm())).ReadAsync(HttpStatusCode.OK);
        var secondFile = await FileKeyAsync(api, id);

        Assert.Equal("VG-02", edited.GetProperty("certification").Text("number"));
        Assert.NotEqual(firstFile, secondFile);
        Assert.False(File.Exists(Path.Combine(api.StorageRoot, firstFile!)));
        Assert.True(File.Exists(Path.Combine(api.StorageRoot, secondFile!)));
        var update = await AuditAsync(api, id, "CooperativeCertification.Updated");
        Assert.Equal(["Number", "File"], Names(update.Before));
        Assert.Equal(("VG-01", "VG-02"), (Prop(update.Before, "Number"), Prop(update.After, "Number")));

        // Remove: the row and its file are gone, and the audit log keeps the values before.
        var removed = await (await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(version)}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Certifications saved.", "MSG-GEN-05"), (removed.Text("message"), removed.Text("messageCode")));
        Assert.Equal(["message", "messageCode"], removed.EnumerateObject().Select(p => p.Name));
        Assert.False(await api.QueryAsync(db => db.CooperativeCertifications.AnyAsync()));
        Assert.False(File.Exists(Path.Combine(api.StorageRoot, secondFile!)));
        var removal = await AuditAsync(api, id, "CooperativeCertification.Removed");
        Assert.Null(removal.After);
        Assert.Equal(("Chứng nhận VietGAP", "VG-02", secondFile), (Prop(removal.Before, "Name"), Prop(removal.Before, "Number"), Prop(removal.Before, "File")));
        var created = await AuditAsync(api, id, "CooperativeCertification.Created");
        Assert.Null(created.Before);
        Assert.Equal(("Chứng nhận VietGAP", "VG-01"), (Prop(created.After, "Name"), Prop(created.After, "Number")));
        Assert.Empty((await (await owner.GetAsync(Certifications)).ReadAsync(HttpStatusCode.OK)).EnumerateArray());
    }

    // BR-FARM-06: Expiring soon when 30 days or fewer remain (the expiry day included), Expired from the next day.
    [Fact]
    public async Task State_is_worked_out_from_the_expiry_date_at_the_30_day_boundary()
    {
        using var api = new StillClockApiFactory(DateTimeOffset.UtcNow);
        var owner = await api.NewOwnerAsync();
        var today = api.Today;
        var cases = new (int? Days, string State)[] { (31, "Valid"), (30, "ExpiringSoon"), (1, "ExpiringSoon"), (0, "ExpiringSoon"), (-1, "Expired"), (null, "Valid") };

        static string NameOf(int? days) => $"Cert {days?.ToString() ?? "without expiry"}";
        foreach (var (days, state) in cases)
        {
            var saved = await owner.CreateCertificationAsync(new CertificationFields { Name = NameOf(days), ExpiryDate = days is { } d ? today.AddDays(d) : null });
            Assert.True(state == saved.Text("state"), $"{days} days: expected {state}, got {saved.Text("state")}");
        }

        var listed = (await (await owner.GetAsync(Certifications)).ReadAsync(HttpStatusCode.OK)).EnumerateArray()
            .ToDictionary(c => c.Text("name"), c => c.Text("state"));
        Assert.Equal(cases.ToDictionary(c => NameOf(c.Days), c => c.State), listed);
        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");
        Assert.Equal(today, DateOnly.Parse(profile.Text("asOf")));
        Assert.Equal(6, profile.GetProperty("certifications").GetArrayLength());
    }

    [Fact]
    public async Task Every_invalid_field_is_named_and_nothing_is_saved()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();

        var tooLong = await owner.AddCertificationAsync(new CertificationFields
        {
            Name = new string('n', 201), Number = new string('9', 101), Issuer = new string('i', 201),
        });
        var blank = await owner.AddCertificationAsync(new CertificationFields { Name = "  ", File = null });
        var notADocument = await owner.AddCertificationAsync(new CertificationFields { File = TextPretendingToBeAFile, FileName = "cert.pdf" });
        var tooBig = await owner.AddCertificationAsync(new CertificationFields { File = Sized(Pdf, 10 * 1024 * 1024 + 1) });

        Assert.Equal(["Name", "Number", "Issuer"], await tooLong.InvalidFieldsAsync());
        Assert.Equal(["Name", "File"], await blank.InvalidFieldsAsync());
        Assert.Equal(["File"], await notADocument.InvalidFieldsAsync());
        Assert.Equal(["File"], await tooBig.InvalidFieldsAsync());
        Assert.False(await api.QueryAsync(db => db.CooperativeCertifications.AnyAsync()));
        Assert.False(await api.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.RecordType == nameof(CooperativeCertification))));
        Assert.Empty(api.StoredFiles(FileCategories.CooperativeCertifications));
    }

    // GFR-01: the file is mandatory, so nothing is saved until it is uploaded (MSG-GEN-03, old code MSG05).
    [Fact]
    public async Task Upload_failure_answers_502_msg_gen_03_and_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var existing = await owner.CreateCertificationAsync(new CertificationFields { Number = "VG-OLD" });
        var version = await api.IssueCertificationVersionAsync(existing.Id());
        // A file where the storage folder should be: the Local provider cannot write.
        Directory.Delete(api.StorageRoot, recursive: true);
        await File.WriteAllTextAsync(api.StorageRoot, "not a folder");
        try
        {
            var add = await owner.AddCertificationAsync(new CertificationFields());
            var edit = await owner.PutAsync($"{Certifications}/{existing.Id()}", new CertificationFields { Number = "VG-NEW", RowVersion = version }.ToForm());

            foreach (var response in new[] { add, edit })
            {
                var problem = await response.ReadAsync(HttpStatusCode.BadGateway);
                Assert.Equal(("MSG-GEN-03", "MSG05"), (problem.Text("code"), problem.Text("legacyCode")));
                Assert.Equal("The file could not be uploaded. Your other entries are kept; please try again.", problem.Text("detail"));
            }

            var stored = await api.QueryAsync(db => db.CooperativeCertifications.AsNoTracking().ToListAsync());
            Assert.Equal(["VG-OLD"], stored.Select(c => c.Number));
            Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(CooperativeCertification))));
        }
        finally
        {
            File.Delete(api.StorageRoot);
        }
    }

    [Fact]
    public async Task Edit_and_remove_need_the_current_row_version_and_an_existing_certification()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var id = (await owner.CreateCertificationAsync()).Id();
        await api.IssueCertificationVersionAsync(id);

        var staleEdit = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields { File = null, RowVersion = AcsTestKit.StaleRowVersion }.ToForm());
        var missingEdit = await owner.PutAsync($"{Certifications}/{id}", new CertificationFields { File = null }.ToForm());
        var staleRemove = await owner.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(AcsTestKit.StaleRowVersion)}");
        var missingRemove = await owner.DeleteAsync($"{Certifications}/{id}");
        var unknown = await owner.DeleteAsync($"{Certifications}/{Guid.NewGuid()}?rowVersion={Uri.EscapeDataString(AcsTestKit.StaleRowVersion)}");

        Assert.Equal("MSG06", await staleEdit.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
        Assert.Equal(["RowVersion"], await missingEdit.InvalidFieldsAsync());
        Assert.Equal("MSG06", await staleRemove.ErrorCodeAsync());
        Assert.Equal(["RowVersion"], await missingRemove.InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.CooperativeCertifications.CountAsync()));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(CooperativeCertification))));
    }

    // GFR-02: staff (whatever their pairs) and traders get MSG-GEN-08 and change nothing; anonymous callers get 401.
    [Fact]
    public async Task Only_the_owner_lists_adds_edits_or_removes_certifications()
    {
        using var api = new AuthApiFactory();
        var owner = await api.NewOwnerAsync();
        var id = (await owner.CreateCertificationAsync()).Id();
        var others = new[]
        {
            await api.NonOwnerAsync(SubjectType.Staff, StaffRole.ZoneManager),
            await api.NonOwnerAsync(SubjectType.Staff, StaffRole.Accountant),
            await api.NonOwnerAsync(SubjectType.Trader),
        };

        foreach (var client in others)
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync(Certifications)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.AddCertificationAsync(new CertificationFields())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsync($"{Certifications}/{id}", new CertificationFields { RowVersion = AcsTestKit.StaleRowVersion }.ToForm())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.DeleteAsync($"{Certifications}/{id}?rowVersion={Uri.EscapeDataString(AcsTestKit.StaleRowVersion)}")).ErrorCodeAsync());
        }

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Certifications)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.AddCertificationAsync(new CertificationFields())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Certifications}/{id}")).StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.CooperativeCertifications.CountAsync()));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(CooperativeCertification))));
    }

    private static Task<string?> FileKeyAsync(AuthApiFactory api, Guid id) =>
        api.QueryAsync(db => db.CooperativeCertifications.Where(c => c.Id == id).Select(c => c.FileUrl).SingleAsync());

    private static Task<OMTS.Domain.Entities.Platform.AuditEvent> AuditAsync(AuthApiFactory api, Guid id, string action) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == id && e.Action == action));

    private static List<string> Names(string? json) => JsonDocument.Parse(json!).RootElement.EnumerateObject().Select(p => p.Name).ToList();

    private static string? Prop(string? json, string name) => JsonDocument.Parse(json!).RootElement.GetProperty(name).GetString();
}
