using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-217 PR 0.8, FARM-02 and FARM-03: typed zone codes against the proposal (GBR-03), who is refused first,
/// the deprecated <c>zoneIds</c> field, the order of the refusals and a zone without a water measurement point.
/// </summary>
public class ZonesLicensesQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // GBR-03: the Owner may type a code; the proposal then moves on, and only codes still in use count.
    [Fact]
    public async Task Typed_codes_move_the_proposal_and_a_taken_code_is_refused_with_the_existing_zone()
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();

        var typed = await owner.Client.CreateZoneAsync(NewZoneBody(" kv12 "));
        Assert.Equal("kv12", typed.Text("code"));
        Assert.Equal("KV13", await NextCodeAsync(owner.Client));

        var proposal = await owner.Client.CreateZoneAsync(NewZoneBody("KV13"));
        var duplicate = await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody("kv13"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-02", proposal.Id()), (duplicate.Text("code"), duplicate.GetProperty("existingId").GetGuid()));

        // A code changed away from the pattern is free again; the proposal never equals a code in use.
        await (await owner.Client.PutAsJsonAsync($"/api/zones/{proposal.Id()}", NewZoneBody("Bai-Tay") with { RowVersion = proposal.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal("KV13", await NextCodeAsync(owner.Client));
        Assert.Equal("KV13", (await owner.Client.CreateZoneAsync(NewZoneBody("KV13"))).Text("code"));
    }

    // FARM-02.0 step 3: the proposal goes in the Code field, so it must be a code that can be saved (at most 50 characters).
    [Fact]
    public async Task The_proposal_after_the_longest_numeric_code_can_still_be_saved()
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();
        await owner.Client.CreateZoneAsync(NewZoneBody("KV" + new string('9', 48)));

        var next = await NextCodeAsync(owner.Client);

        await owner.Client.CreateZoneAsync(NewZoneBody(next));
    }

    // GFR-02 before GBR-01: a refused caller learns nothing about the status; anonymous callers stop at 401 on every new route.
    [Fact]
    public async Task Permission_is_checked_before_the_status_and_anonymous_callers_are_refused_on_every_route()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1) });
        var staff = await factory.StaffAsync(await factory.ZoneEntityAsync(zone.Id()));
        var discontinued = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);
        var body = NewZoneBody() with { RowVersion = discontinued.Text("rowVersion") };

        Assert.Equal("MSG-GEN-08", await (await staff.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await staff.Client.PostAsync("/api/zone-licenses", new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today }.ToForm())).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await staff.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", new LicenseFields { EffectiveDate = Today }.ToForm())).ErrorCodeAsync());

        var anonymous = factory.CreateClient();
        foreach (var response in new[]
        {
            await anonymous.GetAsync("/api/zones/next-code"),
            await anonymous.PostAsJsonAsync($"/api/zones/{zone.Id()}/resume", new { }),
            await anonymous.PostAsJsonAsync($"/api/zones/{zone.Id()}/reactivate", new { }),
            await anonymous.PostAsync("/api/zone-licenses", new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today }.ToForm()),
            await anonymous.PostAsync($"/api/zone-licenses/{license.Id()}/replace", new LicenseFields { EffectiveDate = Today }.ToForm()),
            await anonymous.GetAsync($"/api/zones/{zone.Id()}/licenses"),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // The client change list: zoneIds is read with one value only, "two or more answer 400 Zone".
    [Fact]
    public async Task Several_zones_in_the_deprecated_field_are_refused_even_next_to_zone_id()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { EffectiveDate = Today.AddYears(-1) };

        Assert.Equal(["Zone"], await (await owner.Client.PostAsync("/api/zone-licenses", (fields with { ZoneId = zoneA, ZoneIds = [zoneA, zoneB] }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.Client.PostAsync("/api/zone-licenses", (fields with { ZoneId = zoneA, ZoneIds = [zoneB] }).ToForm())).InvalidFieldsAsync());
    }

    [Fact]
    public async Task The_deprecated_field_counts_one_zone_once_and_an_empty_or_malformed_zone_is_refused()
    {
        var owner = await factory.OwnerAsync();
        var zone = (await owner.Client.CreateZoneAsync()).Id();
        var other = (await owner.Client.CreateZoneAsync()).Id();
        var fields = new LicenseFields { EffectiveDate = Today.AddYears(-1) };

        var repeated = await owner.Client.CreateLicenseAsync(fields with { ZoneIds = [zone, zone] });
        Assert.Equal(zone, repeated.GetProperty("zone").Id());

        Assert.Equal(["Zone"], await (await owner.Client.PostAsync("/api/zone-licenses", (fields with { ZoneId = Guid.Empty }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(["Zone"], await (await owner.Client.PostAsync($"/api/zone-licenses/{repeated.Id()}/replace", (fields with { ZoneIds = [other] }).ToForm())).InvalidFieldsAsync());

        var malformed = fields.ToForm();
        malformed.Add(new StringContent("not-a-guid"), "zoneId");
        var refused = await owner.Client.PostAsync("/api/zone-licenses", malformed);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zone)));
    }

    // FARM-03.0.E1 on a replace: nothing is saved, the old license stays not Replaced and no file is left.
    [Fact]
    public async Task A_refused_replace_leaves_the_license_replaceable_and_stores_no_file()
    {
        var owner = await factory.OwnerAsync();
        var zone = (await owner.Client.CreateZoneAsync()).Id();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = zone, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(40) });
        var stored = factory.StoredFiles("zone-licenses").Count;

        var refused = new LicenseFields { EffectiveDate = Today, ExpiryDate = Today };
        Assert.Equal(["Expiry date"], await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", refused.ToForm())).InvalidFieldsAsync());
        Assert.Equal(["Expiry date"], await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (refused with { ExpiryDate = null }).ToForm())).InvalidFieldsAsync());
        Assert.Equal(["File"], await (await owner.Client.PostAsync($"/api/zone-licenses/{license.Id()}/replace", (refused with { ExpiryDate = Today.AddYears(1), File = TextPretendingToBeAFile }).ToForm())).InvalidFieldsAsync());

        Assert.Equal(stored, factory.StoredFiles("zone-licenses").Count);
        Assert.Equal(1, await factory.QueryAsync(db => db.ZoneComplianceDocuments.CountAsync(d => d.ZoneId == zone)));
        Assert.Equal("Valid", (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/zone-licenses/{license.Id()}")).Text("state"));
    }

    // FARM-02.1.E1 and FARM-03.0.E2 come before the version check; a Replaced license in a discontinued zone names its replacement.
    [Fact]
    public async Task The_zone_status_is_refused_before_a_stale_version_and_a_replaced_license_keeps_its_own_answer()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var fields = new LicenseFields { ZoneId = zone.Id(), EffectiveDate = Today.AddYears(-1) };
        var old = await owner.Client.CreateLicenseAsync(fields);
        var replacement = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", (fields with { ZoneId = null }).ToForm())).ReadAsync(HttpStatusCode.Created);
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var stale = await (await owner.Client.PutAsync($"/api/zone-licenses/{replacement.Id()}", (fields with { RowVersion = StaleRowVersion }).ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", stale.Text("code"));
        Assert.Contains("is Discontinued", stale.Text("detail"));

        var replaced = await (await owner.Client.PutAsync($"/api/zone-licenses/{old.Id()}", (fields with { RowVersion = StaleRowVersion }).ToForm())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", replacement.Id()), (replaced.Text("code"), replaced.GetProperty("replacementId").GetGuid()));
    }

    // FARM-02.0 step 6 and FARM-02.1: "shows that the zone is saved (MSG-GEN-05)", as the other FARM-01 saves answer it.
    [Fact]
    public async Task A_saved_zone_answers_msg_gen_05()
    {
        var owner = await factory.OwnerAsync();
        var body = NewZoneBody();

        var created = await (await owner.Client.PostAsJsonAsync("/api/zones", body)).ReadAsync(HttpStatusCode.Created);
        var zone = created.GetProperty("zone");
        var edited = await (await owner.Client.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Renamed", RowVersion = zone.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);

        // The shape of the FARM-01 saves: { zone, message, messageCode }.
        Assert.Equal(("MSG-GEN-05", "Farming zone saved.", body.Name), (created.Text("messageCode"), created.Text("message"), zone.Text("name")));
        Assert.Equal(("MSG-GEN-05", "Farming zone saved.", "Renamed"), (edited.Text("messageCode"), edited.Text("message"), edited.GetProperty("zone").Text("name")));
    }

    // The point became optional: recording water in a zone without one still works and stores no point (WATER-01).
    [Fact]
    public async Task Water_can_be_recorded_in_a_zone_without_a_measurement_point()
    {
        using var api = new AuthApiFactory();
        var water = await api.ZoneAsync();
        await api.UpdateAsync<FarmingZone>(water.Zone.Id, z => z.WaterMeasurementPoint = null);

        var recorded = await water.RecordedAsync(7.8m, 20m);

        Assert.Null(await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == recorded.Id()).Select(m => m.MeasurementPoint).SingleAsync()));
    }

    private static async Task<string> NextCodeAsync(HttpClient owner) =>
        (await (await owner.GetAsync("/api/zones/next-code")).ReadAsync(HttpStatusCode.OK)).Text("code");
}
