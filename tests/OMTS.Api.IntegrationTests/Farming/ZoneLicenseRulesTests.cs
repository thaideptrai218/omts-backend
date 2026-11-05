using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>FARM-03 rules and error bullets: validation, real file checks, multi-zone scope, edit, replace chain, states as of a date, Owner only.</summary>
public class ZoneLicenseRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Create_lists_every_invalid_field_and_saves_nothing()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var number = $"GP-{NewCode()}";
        var stored = factory.StoredFiles("zone-licenses").Count;

        var fields = await (await owner.Client.PostAsync("/api/zone-licenses", new LicenseFields
        {
            DocumentType = "BUILDING_PERMIT", DocumentNumber = " ", EffectiveDate = null, ExpiryDate = Today, IssuingAuthority = new string('i', 201),
            Holder = new string('h', 201), LegalScope = new string('s', 1001), File = null,
        }.ToForm())).InvalidFieldsAsync();
        Assert.Equal(["Document type", "Document number", "Effective date", "Issuing authority", "Holder", "Legal scope", "Zones", "File"], fields);

        var valid = new LicenseFields { DocumentNumber = number, ZoneIds = [zoneId], EffectiveDate = Today, ExpiryDate = Today.AddYears(1) };
        Assert.Equal(["Expiry date"], await Invalid(valid with { ExpiryDate = Today }));
        Assert.Equal(["Expiry date"], await Invalid(valid with { ExpiryDate = Today.AddDays(-1) }));
        Assert.Equal(["Zones"], await Invalid(valid with { ZoneIds = [zoneId, Guid.NewGuid()] }));
        Assert.Equal(["Document number"], await Invalid(valid with { DocumentNumber = new string('n', 101) }));

        Assert.Equal(0, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.DocumentNumber == number)));
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordCode == number)));
        Assert.Equal(stored, factory.StoredFiles("zone-licenses").Count);

        // The longest allowed values and a lower-case type are accepted.
        var longest = await owner.Client.CreateLicenseAsync(valid with
        {
            DocumentType = "sea_area_allocation", DocumentNumber = $"{number}{new string('n', 100 - number.Length)}",
            IssuingAuthority = new string('i', 200), Holder = new string('h', 200), LegalScope = new string('s', 1000),
        });
        Assert.Equal("SEA_AREA_ALLOCATION", longest.Text("documentType"));
        Assert.Equal("Sea area allocation decision", longest.Text("documentTypeName"));

        async Task<List<string>> Invalid(LicenseFields form) => await (await owner.Client.PostAsync("/api/zone-licenses", form.ToForm())).InvalidFieldsAsync();
    }

    [Fact]
    public async Task File_must_really_be_pdf_jpeg_or_png_of_at_most_10_MB()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var form = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddYears(-1) };
        var stored = factory.StoredFiles("zone-licenses").Count;

        foreach (var (bytes, name) in new[]
        {
            (TextPretendingToBeAFile, "license.pdf"), (WebP, "license.webp"), (Sized(Pdf, 10 * 1024 * 1024 + 1), "big.pdf"), (Array.Empty<byte>(), "empty.pdf"),
        })
        {
            Assert.Equal(["File"], await (await owner.Client.PostAsync("/api/zone-licenses", (form with { File = bytes, FileName = name }).ToForm())).InvalidFieldsAsync());
        }

        Assert.Equal(stored, factory.StoredFiles("zone-licenses").Count);

        await owner.Client.CreateLicenseAsync(form with { File = Jpeg, FileName = "scan.pdf" });
        await owner.Client.CreateLicenseAsync(form with { File = Png, FileName = "scan" });
        await owner.Client.CreateLicenseAsync(form with { File = Sized(Pdf, 10 * 1024 * 1024), FileName = "exactly-10-mb.pdf" });
        Assert.Equal(stored + 3, factory.StoredFiles("zone-licenses").Count);
    }

    [Fact]
    public async Task One_license_covers_several_zones_with_one_scope_per_zone()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var effective = Today.AddDays(-100);
        var expiry = Today.AddDays(200);

        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneA, zoneB], EffectiveDate = effective, ExpiryDate = expiry });

        Assert.Equal([zoneA, zoneB], license.GetProperty("zones").EnumerateArray().Select(z => z.Id()));
        Assert.Equal("Valid", license.Text("state"));
        var scopes = await factory.QueryAsync(db => db.DocumentZoneScopes.Where(s => s.DocumentId == license.Id()).ToListAsync());
        Assert.Equal(new[] { zoneA, zoneB }.Order(), scopes.Select(s => s.ZoneId).Order());
        Assert.All(scopes, s => Assert.Equal((effective, expiry), (s.ValidFrom!.Value, s.ValidTo!.Value)));
        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.DocumentNumber == license.Text("documentNumber"))));

        foreach (var zoneId in new[] { zoneA, zoneB })
        {
            var overview = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses");
            Assert.Contains(overview.GetProperty("licenses").EnumerateArray(), r => r.TryGetProperty("documentId", out var id) && id.ValueKind == JsonValueKind.String && id.GetGuid() == license.Id());
        }

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == license.Id()));
        Assert.Equal("ZoneLicense.Created", audit.Action);
        Assert.Equal(license.Text("documentNumber"), audit.RecordCode);
        Assert.Equal(zoneA, audit.ZoneId);
        Assert.Equal(new[] { zoneA, zoneB }.Order(), JsonNode.Parse(audit.After!)!["ZoneIds"]!.AsArray().Select(z => z!.GetValue<Guid>()));
    }

    [Fact]
    public async Task Edit_syncs_zones_re_arms_reminders_on_a_new_expiry_and_keeps_the_old_file_in_the_audit()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var zoneC = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneIds = [zoneA, zoneB], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(20) };
        var license = await owner.Client.CreateLicenseAsync(fields);
        await factory.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(Today);
        var reminded = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.NotNull(reminded.ExpiringReminderSentAt);
        var oldFile = reminded.EvidenceFileUrl!;

        Assert.Equal("MSG06", await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { RowVersion = StaleRowVersion }).ToForm())).ErrorCodeAsync());
        Assert.Equal(["Expiry date"], await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ExpiryDate = Today.AddDays(-301), RowVersion = license.Text("rowVersion") }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PutAsync($"/api/zone-licenses/{Guid.NewGuid()}", (fields with { RowVersion = "" }).ToForm())).StatusCode);

        var version = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id());
        var edited = await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            ZoneIds = [zoneB, zoneC], ExpiryDate = Today.AddDays(25), Holder = "HTX Trung Nam", File = Png, FileName = "renewed.png", RowVersion = version,
        }).ToForm())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([zoneB, zoneC], edited.GetProperty("zones").EnumerateArray().Select(z => z.Id()));

        var saved = await factory.QueryAsync(db => db.ZoneComplianceDocuments.Include(d => d.ZoneScopes).SingleAsync(d => d.Id == license.Id()));
        Assert.Equal(new[] { zoneB, zoneC }.Order(), saved.ZoneScopes.Select(s => s.ZoneId).Order());
        Assert.All(saved.ZoneScopes, s => Assert.Equal(Today.AddDays(25), s.ValidTo));
        Assert.Null(saved.ExpiringReminderSentAt);
        Assert.Null(saved.ExpiredReminderSentAt);
        Assert.NotEqual(oldFile, saved.EvidenceFileUrl);

        var update = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == license.Id() && e.Action == "ZoneLicense.Updated"));
        var before = JsonNode.Parse(update.Before!)!;
        Assert.Equal(oldFile, before["File"]!.GetValue<string>());
        Assert.Equal(new[] { zoneA, zoneB }.Order(), before["ZoneIds"]!.AsArray().Select(z => z!.GetValue<Guid>()));
        Assert.Equal(saved.EvidenceFileUrl, JsonNode.Parse(update.After!)!["File"]!.GetValue<string>());
        Assert.Equal(zoneB, update.ZoneId);

        // Without a new file the stored file stays; the same expiry date keeps the markers.
        await factory.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(Today);
        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            ZoneIds = [zoneB, zoneC], ExpiryDate = Today.AddDays(25), Holder = "Other holder", File = null, RowVersion = edited.Text("rowVersion"),
        }).ToForm())).ReadAsync(HttpStatusCode.OK);
        var kept = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.Equal(saved.EvidenceFileUrl, kept.EvidenceFileUrl);
        Assert.NotNull(kept.ExpiringReminderSentAt);
    }

    [Fact]
    public async Task Replace_defaults_to_the_old_zones_leaves_the_old_license_unchanged_and_only_the_latest_can_be_replaced()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var old = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            DocumentType = "SEA_AREA_ALLOCATION", ZoneIds = [zoneA, zoneB], EffectiveDate = Today.AddYears(-5), ExpiryDate = Today.AddDays(10),
        });
        var renewal = new LicenseFields { DocumentType = null, EffectiveDate = Today.AddDays(-1), ExpiryDate = Today.AddYears(5) };

        Assert.Equal(["File"], await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", (renewal with { File = null }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsync($"/api/zone-licenses/{Guid.NewGuid()}/replace", renewal.ToForm())).StatusCode);

        var replacement = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("SEA_AREA_ALLOCATION", replacement.Text("documentType"));
        Assert.Equal(new[] { zoneA, zoneB }.Order(), replacement.GetProperty("zones").EnumerateArray().Select(z => z.Id()).Order());
        Assert.Equal(old.Id(), replacement.GetProperty("replacesDocumentId").GetGuid());

        var oldNow = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{old.Id()}");
        Assert.Equal(old.Text("expiryDate"), oldNow.Text("expiryDate"));
        Assert.Equal(old.Text("effectiveDate"), oldNow.Text("effectiveDate"));
        Assert.Equal(replacement.Id(), oldNow.GetProperty("replacedByDocumentId").GetGuid());
        Assert.Equal("Superseded", oldNow.Text("state"));

        var again = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", again.Text("code"));
        Assert.Equal(replacement.Id(), again.GetProperty("replacementId").GetGuid());

        var edit = await (await owner.Client.PutAsync($"/api/zone-licenses/{old.Id()}", new LicenseFields
        {
            ZoneIds = [zoneA], EffectiveDate = Today.AddYears(-5), ExpiryDate = Today.AddDays(40), RowVersion = old.Text("rowVersion"),
        }.ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", edit.Text("code"));
        Assert.Contains(replacement.Text("documentNumber"), edit.Text("detail"));
        Assert.Equal(replacement.Id(), edit.GetProperty("replacementId").GetGuid());

        // The chain goes on from its latest license only; zones may also be chosen explicitly.
        var third = await (await owner.Client.PostAsync($"/api/zone-licenses/{replacement.Id()}/replace", (renewal with { ZoneIds = [zoneB] }).ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal([zoneB], third.GetProperty("zones").EnumerateArray().Select(z => z.Id()));

        var replaced = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == replacement.Id()));
        Assert.Equal("ZoneLicense.Replaced", replaced.Action);
        Assert.Equal(old.Id(), JsonNode.Parse(replaced.Before!)!["ReplacedDocumentId"]!.GetValue<Guid>());
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == old.Id() && e.Action != "ZoneLicense.Created")));
    }

    [Fact]
    public async Task States_and_missing_types_follow_the_date_asked_for()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(30) });

        Assert.Equal("Valid", await StateAsync(Today.AddDays(-1)));
        Assert.Equal("ExpiringSoon", await StateAsync(Today));
        Assert.Equal("ExpiringSoon", await StateAsync(Today.AddDays(30)));
        Assert.Equal("Expired", await StateAsync(Today.AddDays(31)));
        Assert.Equal("NotYetEffective", await StateAsync(Today.AddDays(-301)));
        Assert.Equal(["SEA_AREA_ALLOCATION"], await MissingAsync(Today));
        Assert.Equal(["AQUACULTURE_LICENSE", "SEA_AREA_ALLOCATION"], await MissingAsync(Today.AddDays(31)));

        // A renewal effective in 10 days supersedes the old license from that day on.
        var renewal = await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", new LicenseFields
        {
            EffectiveDate = Today.AddDays(10), ExpiryDate = Today.AddYears(3),
        }.ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("ExpiringSoon", await StateAsync(Today.AddDays(9)));
        Assert.Equal("Superseded", await StateAsync(Today.AddDays(10)));
        Assert.Equal("NotYetEffective", await StateAsync(Today.AddDays(9), renewal.Id()));
        Assert.Equal("Valid", await StateAsync(Today.AddDays(10), renewal.Id()));
        Assert.Equal(["SEA_AREA_ALLOCATION"], await MissingAsync(Today.AddDays(9)));
        Assert.Equal(["SEA_AREA_ALLOCATION"], await MissingAsync(Today.AddDays(40)));

        var detail = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}?asOf={Today.AddDays(10):yyyy-MM-dd}");
        Assert.Equal("Superseded", detail.Text("state"));
        Assert.Equal(Today.AddDays(10).ToString("yyyy-MM-dd"), detail.Text("asOf"));

        async Task<string> StateAsync(DateOnly asOf, Guid? documentId = null)
        {
            var overview = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses?asOf={asOf:yyyy-MM-dd}");
            Assert.Equal(asOf.ToString("yyyy-MM-dd"), overview.Text("asOf"));
            return overview.GetProperty("licenses").EnumerateArray()
                .Single(r => r.GetProperty("documentId").ValueKind == JsonValueKind.String && r.GetProperty("documentId").GetGuid() == (documentId ?? license.Id()))
                .Text("state");
        }

        async Task<List<string>> MissingAsync(DateOnly asOf) =>
            (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses?asOf={asOf:yyyy-MM-dd}")).GetProperty("licenses").EnumerateArray()
                .Where(r => r.Text("state") == "Missing").Select(r => r.Text("documentType")).Order().ToList();
    }

    [Fact]
    public async Task Licenses_are_owner_only_even_for_staff_holding_every_function_in_the_zone()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today });
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneId));
        var everywhere = await factory.StaffAsync(null);
        var trader = await factory.TraderAsync();
        var form = new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today, RowVersion = license.Text("rowVersion") };

        foreach (var client in new[] { staff.Client, everywhere.Client, trader })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/zones/{zoneId}/licenses")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/zone-licenses/{license.Id()}")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsync("/api/zone-licenses", form.ToForm())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsync($"/api/zone-licenses/{license.Id()}", form.ToForm())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", form.ToForm())).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/zone-licenses/{license.Id()}")).StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ZoneScopes.Any(s => s.ZoneId == zoneId))));
    }
}
