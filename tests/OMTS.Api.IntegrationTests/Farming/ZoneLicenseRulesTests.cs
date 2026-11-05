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

/// <summary>FARM-03 rules and error bullets: validation, real file checks, one zone per license, edit, replace chain, states as of a date, Owner only.</summary>
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
            DocumentType = "BUILDING_PERMIT", DocumentNumber = " ", EffectiveDate = null, ExpiryDate = null, IssuingAuthority = new string('i', 201),
            Holder = new string('h', 201), LegalScope = new string('s', 1001), File = null,
        }.ToForm())).InvalidFieldsAsync();
        Assert.Equal(["Document type", "Document number", "Effective date", "Expiry date", "Issuing authority", "Holder", "Legal scope", "Zone", "File"], fields);

        var valid = new LicenseFields { DocumentNumber = number, ZoneId = zoneId, EffectiveDate = Today, ExpiryDate = Today.AddYears(1) };
        Assert.Equal(["Expiry date"], await Invalid(valid with { ExpiryDate = Today }));
        Assert.Equal(["Expiry date"], await Invalid(valid with { ExpiryDate = Today.AddDays(-1) }));
        Assert.Equal(["Expiry date"], await Invalid(valid with { ExpiryDate = null }));
        Assert.Equal(["Zone"], await Invalid(valid with { ZoneId = Guid.NewGuid() }));
        Assert.Equal(["Document number"], await Invalid(valid with { DocumentNumber = new string('n', 101) }));

        Assert.Equal(0, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.DocumentNumber == number)));
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordCode == number)));
        Assert.Equal(stored, factory.StoredFiles("zone-licenses").Count);

        // The longest allowed values and a lower-case type are accepted; the type, holder and legal scope are optional.
        var longest = await owner.Client.CreateLicenseAsync(valid with
        {
            DocumentType = "sea_area_allocation", DocumentNumber = $"{number}{new string('n', 100 - number.Length)}",
            IssuingAuthority = new string('i', 200), Holder = new string('h', 200), LegalScope = new string('s', 1000),
        });
        Assert.Equal("SEA_AREA_ALLOCATION", longest.Text("documentType"));
        Assert.Equal("Sea area allocation decision", longest.Text("documentTypeName"));
        var bare = await owner.Client.CreateLicenseAsync(valid with { DocumentNumber = $"GP-{NewCode()}", DocumentType = null, Holder = null, LegalScope = null });
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (bare.GetProperty("documentType").ValueKind, bare.GetProperty("documentTypeName").ValueKind, bare.GetProperty("holder").ValueKind, bare.GetProperty("legalScope").ValueKind));

        async Task<List<string>> Invalid(LicenseFields form) => await (await owner.Client.PostAsync("/api/zone-licenses", form.ToForm())).InvalidFieldsAsync();
    }

    [Fact]
    public async Task File_must_really_be_pdf_jpeg_or_png_of_at_most_10_MB()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var form = new LicenseFields { ZoneId = zoneId, EffectiveDate = Today.AddYears(-1) };
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

    // §I ZoneLicense: a license of one farming zone. The deprecated zoneIds field is still read, with one zone only.
    [Fact]
    public async Task A_license_belongs_to_one_zone_that_never_changes()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = await owner.Client.CreateZoneAsync();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneId = zoneA.Id(), EffectiveDate = Today.AddDays(-100), ExpiryDate = Today.AddDays(200) };

        var license = await owner.Client.CreateLicenseAsync(fields);

        Assert.Equal((zoneA.Id(), zoneA.Text("code")), (license.GetProperty("zone").Id(), license.GetProperty("zone").Text("code")));
        Assert.Equal([zoneA.Id()], license.GetProperty("zones").EnumerateArray().Select(z => z.Id()));
        Assert.Equal(zoneA.Id(), (await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()))).ZoneId);
        Assert.Contains(await LicenseIdsAsync(owner.Client, zoneA.Id()), id => id == license.Id());
        Assert.DoesNotContain(await LicenseIdsAsync(owner.Client, zoneB), id => id == license.Id());

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == license.Id()));
        Assert.Equal(("ZoneLicense.Created", license.Text("documentNumber"), zoneA.Id()), (audit.Action, audit.RecordCode, audit.ZoneId));
        Assert.Equal(zoneA.Id(), JsonNode.Parse(audit.After!)!["ZoneId"]!.GetValue<Guid>());

        // The older field with one zone still works; with two zones it is refused.
        var legacy = await owner.Client.CreateLicenseAsync(fields with { ZoneId = null, ZoneIds = [zoneB] });
        Assert.Equal(zoneB, legacy.GetProperty("zone").Id());
        Assert.Equal(["Zone"], await (await owner.Client.PostAsync("/api/zone-licenses", (fields with { ZoneId = null, ZoneIds = [zoneA.Id(), zoneB] }).ToForm())).InvalidFieldsAsync());

        // Edit and replace keep the zone: left out it stays, another one is refused.
        var version = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id());
        Assert.Equal(["Zone"], await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = zoneB, RowVersion = version }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = zoneB }).ToForm())).InvalidFieldsAsync());
        var edited = await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ZoneId = null, Holder = "HTX", File = null, RowVersion = version }).ToForm()))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(zoneA.Id(), edited.GetProperty("zone").Id());
        var replacement = await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (fields with { ZoneId = null }).ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(zoneA.Id(), replacement.GetProperty("zone").Id());
    }

    [Fact]
    public async Task Edit_re_arms_reminders_on_a_new_expiry_and_keeps_the_old_file_in_the_audit()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { ZoneId = zoneId, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(20) };
        var license = await owner.Client.CreateLicenseAsync(fields);
        await factory.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(Today);
        var reminded = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.NotNull(reminded.ExpiringReminderSentAt);
        var oldFile = reminded.EvidenceFileUrl!;

        Assert.Equal("MSG06", await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { RowVersion = StaleRowVersion }).ToForm())).ErrorCodeAsync());
        Assert.Equal(["Expiry date"], await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { ExpiryDate = Today.AddDays(-301), RowVersion = license.Text("rowVersion") }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PutAsync($"/api/zone-licenses/{Guid.NewGuid()}", (fields with { RowVersion = "" }).ToForm())).StatusCode);

        var version = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id());
        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            ExpiryDate = Today.AddDays(25), Holder = "HTX Trung Nam", File = Png, FileName = "renewed.png", RowVersion = version,
        }).ToForm())).ReadAsync(HttpStatusCode.OK);

        var saved = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.Null(saved.ExpiringReminderSentAt);
        Assert.Null(saved.ExpiredReminderSentAt);
        Assert.NotEqual(oldFile, saved.EvidenceFileUrl);

        var update = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == license.Id() && e.Action == "ZoneLicense.Updated"));
        var before = JsonNode.Parse(update.Before!)!;
        Assert.Equal(oldFile, before["File"]!.GetValue<string>());
        Assert.Equal(saved.EvidenceFileUrl, JsonNode.Parse(update.After!)!["File"]!.GetValue<string>());
        Assert.Equal(zoneId, update.ZoneId);

        // Without a new file the stored file stays; the same expiry date keeps the markers.
        await factory.Services.GetRequiredService<ZoneLicenseReminderJob>().RunOnceAsync(Today);
        var current = await factory.IssueRowVersionAsync<ZoneComplianceDocument>(license.Id());
        await (await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with
        {
            ExpiryDate = Today.AddDays(25), Holder = "Other holder", File = null, RowVersion = current,
        }).ToForm())).ReadAsync(HttpStatusCode.OK);
        var kept = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()));
        Assert.Equal(saved.EvidenceFileUrl, kept.EvidenceFileUrl);
        Assert.NotNull(kept.ExpiringReminderSentAt);
    }

    [Fact]
    public async Task Replace_keeps_the_zone_leaves_the_old_license_unchanged_and_only_a_license_not_replaced_can_be_replaced_or_edited()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var old = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            DocumentType = "SEA_AREA_ALLOCATION", ZoneId = zoneId, EffectiveDate = Today.AddYears(-5), ExpiryDate = Today.AddDays(10),
        });
        var renewal = new LicenseFields { DocumentType = null, EffectiveDate = Today.AddDays(-1), ExpiryDate = Today.AddYears(5) };

        Assert.Equal(["File"], await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", (renewal with { File = null }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostAsync($"/api/zone-licenses/{Guid.NewGuid()}/replace", renewal.ToForm())).StatusCode);

        var replacement = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("SEA_AREA_ALLOCATION", replacement.Text("documentType"));
        Assert.Equal(zoneId, replacement.GetProperty("zone").Id());
        Assert.Equal(old.Id(), replacement.GetProperty("replacesDocumentId").GetGuid());

        var oldNow = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{old.Id()}");
        Assert.Equal(old.Text("expiryDate"), oldNow.Text("expiryDate"));
        Assert.Equal(old.Text("effectiveDate"), oldNow.Text("effectiveDate"));
        Assert.Equal(replacement.Id(), oldNow.GetProperty("replacedByDocumentId").GetGuid());
        Assert.Equal("Replaced", oldNow.Text("state"));

        var again = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", again.Text("code"));
        Assert.Equal(replacement.Id(), again.GetProperty("replacementId").GetGuid());

        // A Replaced license is kept as it is (default of gap Q8): its edit answers MSG-GEN-09 and changes nothing.
        var edit = await (await owner.Client.PutAsync($"/api/zone-licenses/{old.Id()}", new LicenseFields
        {
            ZoneId = zoneId, EffectiveDate = Today.AddYears(-5), ExpiryDate = Today.AddDays(40), RowVersion = old.Text("rowVersion"),
        }.ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", "LICENSE_REPLACED"), (edit.Text("code"), edit.Text("legacyCode")));
        Assert.Contains(replacement.Text("documentNumber"), edit.Text("detail"));
        Assert.Equal(replacement.Id(), edit.GetProperty("replacementId").GetGuid());
        Assert.Equal(Today.AddDays(10), (await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == old.Id()))).ExpiryDate);

        // The chain goes on from its latest license only.
        var third = await (await owner.Client.PostAsync($"/api/zone-licenses/{replacement.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(replacement.Id(), third.GetProperty("replacesDocumentId").GetGuid());

        var replaced = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == replacement.Id()));
        Assert.Equal(("ZoneLicense.Replaced", zoneId), (replaced.Action, replaced.ZoneId));
        Assert.Equal(old.Id(), JsonNode.Parse(replaced.Before!)!["ReplacedDocumentId"]!.GetValue<Guid>());
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == old.Id() && e.Action != "ZoneLicense.Created")));
    }

    // BR-FARM-06: the state follows the dates, except that a license is Replaced from the day its replacement is recorded.
    [Fact]
    public async Task States_follow_the_date_asked_for_and_replaced_starts_on_the_record_day_of_the_replacement()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneId, EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(30) });

        Assert.Equal("Valid", await StateAsync(Today.AddDays(-1)));
        Assert.Equal("ExpiringSoon", await StateAsync(Today));
        Assert.Equal("ExpiringSoon", await StateAsync(Today.AddDays(30)));
        Assert.Equal("Expired", await StateAsync(Today.AddDays(31)));
        Assert.Equal("NotYetEffective", await StateAsync(Today.AddDays(-301)));

        // A renewal effective only in 10 days: the old license is Replaced from today, the record day, whatever the dates.
        var renewal = await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", new LicenseFields
        {
            EffectiveDate = Today.AddDays(10), ExpiryDate = Today.AddYears(3),
        }.ToForm())).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Valid", await StateAsync(Today.AddDays(-1)));
        Assert.Equal("Replaced", await StateAsync(Today));
        Assert.Equal("Replaced", await StateAsync(Today.AddDays(31)));
        Assert.Equal("NotYetEffective", await StateAsync(Today.AddDays(9), renewal.Id()));
        Assert.Equal("Valid", await StateAsync(Today.AddDays(10), renewal.Id()));

        var detail = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}?asOf={Today:yyyy-MM-dd}");
        Assert.Equal(("Replaced", Today.ToString("yyyy-MM-dd")), (detail.Text("state"), detail.Text("asOf")));
        Assert.Equal("Valid", (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}?asOf={Today.AddDays(-1):yyyy-MM-dd}")).Text("state"));

        // Newest first, no "Missing" rows; the newest license that is not Replaced is flagged.
        var rows = (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses")).GetProperty("licenses").EnumerateArray().ToList();
        Assert.Equal([renewal.Id(), license.Id()], rows.Select(r => r.GetProperty("documentId").GetGuid()));
        Assert.Equal([(true, false), (false, true)], rows.Select(r => (r.GetProperty("isNewest").GetBoolean(), r.GetProperty("isReplaced").GetBoolean())));

        async Task<string> StateAsync(DateOnly asOf, Guid? documentId = null)
        {
            var overview = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses?asOf={asOf:yyyy-MM-dd}");
            Assert.Equal(asOf.ToString("yyyy-MM-dd"), overview.Text("asOf"));
            return overview.GetProperty("licenses").EnumerateArray().Single(r => r.GetProperty("documentId").GetGuid() == (documentId ?? license.Id())).Text("state");
        }
    }

    // GBR-01, FARM-03.0.E2: while the zone is Discontinued its licenses cannot be added, edited or replaced (MSG-GEN-09).
    [Fact]
    public async Task License_writes_need_the_zone_in_use()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddYears(1) };
        var license = await owner.Client.CreateLicenseAsync(fields);
        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        var stored = factory.StoredFiles("zone-licenses").Count;

        foreach (var response in new[]
        {
            await owner.Client.PostAsync("/api/zone-licenses", fields.ToForm()),
            // The zone is checked before the fields (FARM-03.0 step 4).
            await owner.Client.PostAsync("/api/zone-licenses", (fields with { DocumentNumber = null, File = null }).ToForm()),
            await owner.Client.PutAsync($"/api/zone-licenses/{license.Id()}", (fields with { Holder = "HTX", RowVersion = license.Text("rowVersion") }).ToForm()),
            await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", fields.ToForm()),
        })
        {
            var problem = await response.ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-GEN-09", problem.Text("code"));
            Assert.Equal($"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action.", problem.Text("detail"));
        }

        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zone.Id())));
        Assert.Equal(stored, factory.StoredFiles("zone-licenses").Count);
        // Reading stays open (a discontinued zone keeps its licenses).
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/api/zones/{zone.Id()}/licenses")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/api/zone-licenses/{license.Id()}")).StatusCode);

        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { rowVersion = discontinued.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        await owner.Client.CreateLicenseAsync(fields);
    }

    [Fact]
    public async Task Licenses_are_owner_only_even_for_staff_holding_every_function_in_the_zone()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zoneId, EffectiveDate = Today });
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneId));
        var everywhere = await factory.StaffAsync(null);
        var trader = await factory.TraderAsync();
        var form = new LicenseFields { ZoneId = zoneId, EffectiveDate = Today, RowVersion = license.Text("rowVersion") };

        foreach (var client in new[] { staff.Client, everywhere.Client, trader })
        {
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/zones/{zoneId}/licenses")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.GetAsync($"/api/zone-licenses/{license.Id()}")).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsync("/api/zone-licenses", form.ToForm())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PutAsync($"/api/zone-licenses/{license.Id()}", form.ToForm())).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", form.ToForm())).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/zone-licenses/{license.Id()}")).StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zoneId)));
    }

    private static async Task<List<Guid>> LicenseIdsAsync(HttpClient owner, Guid zoneId) =>
        (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zoneId}/licenses")).GetProperty("licenses").EnumerateArray()
            .Select(r => r.GetProperty("documentId").GetGuid()).ToList();
}
