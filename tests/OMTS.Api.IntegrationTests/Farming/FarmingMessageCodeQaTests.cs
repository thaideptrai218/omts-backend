using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-216 PR 0.2 on FARM-01 to FARM-04 (SRS 27/10 Part II §1): the codes the steps cite, their filled-in
/// texts, and the legacy code of each context split (MSG11/MSG61 → MSG-GEN-06, MSG61/MSG03/LICENSE_REPLACED → MSG-GEN-09).
/// </summary>
public class FarmingMessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // FARM-04.2.E1 (MSG11 before) and FARM-04.3.E1 (MSG61 before) both name the open cycle with MSG-GEN-06.
    [Fact]
    public async Task Open_cycle_is_msg_gen_06_naming_it_with_the_legacy_code_of_each_call_site()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.Client.CreateRaftAsync(zoneId, body);
        var openedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var cycle = await factory.AddCycleAsync(raft.Id(), openedAt);
        var expected = $"Cannot proceed because 1 records are still in use: cultivation cycle opened {BusinessClock.ToBusinessDate(openedAt):dd/MM/yyyy}. Please handle these records first.";

        var layout = await (await owner.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 3) with { RowVersion = raft.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG11");
        var discontinue = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG61");

        Assert.Equal(expected, layout.Detail());
        Assert.Equal(expected, discontinue.Detail());
        Assert.Equal(cycle.Id, layout.GetProperty("cycleId").GetGuid());
        Assert.Equal(cycle.Id, discontinue.GetProperty("cycleId").GetGuid());
    }

    // FARM-02.2.E1 (MSG-GEN-06, MSG11 before), GBR-01 wrong status (MSG-GEN-09, MSG61 before) naming the zone or raft code.
    [Fact]
    public async Task Zone_and_raft_status_refusals_name_the_record_and_carry_legacy_codes()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id(), NewRaftBody("R-QA"));

        var inUse = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG11");
        Assert.Equal("Cannot proceed because 1 records are still in use: R-QA. Please handle these records first.", inUse.Detail());

        var stopped = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var raftAgain = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = stopped.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");
        Assert.Equal("Raft R-QA is Discontinued and cannot be used for this action.", raftAgain.Detail());

        var zoneStopped = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var zoneAgain = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zoneStopped.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");
        var resumeInStoppedZone = await (await owner.Client.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { rowVersion = stopped.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        var zoneText = $"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action.";
        Assert.Equal(zoneText, zoneAgain.Detail());
        Assert.Equal(zoneText, resumeInStoppedZone.Detail());
    }

    // FARM-02.0.E1 / FARM-04.0.E1: MSG-GEN-02 with the SRS text "Your entries are kept…" (MSG03 before).
    [Fact]
    public async Task Duplicate_zone_and_raft_codes_are_msg_gen_02_with_the_existing_record()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        var raft = await owner.Client.CreateRaftAsync(zone.Id());

        var zoneDuplicate = await (await owner.Client.PostAsJsonAsync("/api/zones", NewZoneBody(zone.Text("code"))))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        var raftDuplicate = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody(raft.Text("code"))))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");

        Assert.Equal("Farming zone already exists. Your entries are kept: change them, or open the existing record.", zoneDuplicate.Detail());
        Assert.Equal(zone.Id(), zoneDuplicate.GetProperty("existingId").GetGuid());
        Assert.Equal("Raft already exists. Your entries are kept: change them, or open the existing record.", raftDuplicate.Detail());
        Assert.Equal(raft.Id(), raftDuplicate.GetProperty("existingId").GetGuid());
    }

    // BR-FARM-06: a Replaced license is MSG-GEN-09 naming both numbers; the legacy code is LICENSE_REPLACED for an edit
    // and MSG03 ("Replacement license already exists") for a second replacement.
    [Fact]
    public async Task Replaced_license_is_msg_gen_09_naming_the_replacement_with_the_legacy_code_of_each_call_site()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var old = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(20) });
        var renewal = new LicenseFields { DocumentType = null, EffectiveDate = Today, ExpiryDate = Today.AddYears(3) };
        var replacement = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm())).ReadAsync(HttpStatusCode.Created);
        var expected = $"License {old.Text("documentNumber")} is Replaced by {replacement.Text("documentNumber")} and cannot be used for this action.";

        var edit = await (await owner.Client.PutAsync($"/api/zone-licenses/{old.Id()}", new LicenseFields
        {
            ZoneIds = [zoneId], EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(40), RowVersion = old.Text("rowVersion"),
        }.ToForm())).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "LICENSE_REPLACED");
        var again = await (await owner.Client.PostAsync($"/api/zone-licenses/{old.Id()}/replace", renewal.ToForm()))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG03");

        Assert.Equal(expected, edit.Detail());
        Assert.Equal(expected, again.Detail());
        Assert.Equal(replacement.Id(), edit.GetProperty("replacementId").GetGuid());
        Assert.Equal(replacement.Id(), again.GetProperty("existingId").GetGuid());
    }

    // GFR-01: a failed upload is MSG-GEN-03 (MSG05 before), 502 kept.
    [Fact]
    public async Task Failed_raft_photo_upload_is_msg_gen_03_with_legacy_msg05()
    {
        using var api = new AuthApiFactory();
        Directory.CreateDirectory(api.StorageRoot);
        await File.WriteAllTextAsync(Path.Combine(api.StorageRoot, "raft-photos"), "blocked");
        var owner = await api.OwnerAsync();
        var raft = await owner.Client.CreateRaftAsync((await owner.Client.CreateZoneAsync()).Id());

        var failed = await (await owner.Client.PostAsync($"/api/rafts/{raft.Id()}/photo", new MultipartFormDataContent { { FileContent(Png), "file", "raft.png" } }))
            .SrsProblemAsync(HttpStatusCode.BadGateway, "MSG-GEN-03", "MSG05");

        Assert.Equal("The file could not be uploaded. Your other entries are kept; please try again.", failed.Detail());
    }

    // FARM-01 step 5: the saved profile carries MSG-GEN-05 next to its text.
    [Fact]
    public async Task Cooperative_profile_save_carries_msg_gen_05()
    {
        var owner = await factory.OwnerAsync();
        var saved = await (await owner.Client.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(await CooperativeProfileRequests.RowVersionAsync(owner.Client))))
            .BodyAsync(HttpStatusCode.OK);

        Assert.Equal(("Cooperative profile saved.", "MSG-GEN-05"), (saved.Str("message"), saved.Str("messageCode")));
    }

    // SRS FARM-04.0.E3 / MSG-GEN-09 context "adding … a raft of a discontinued zone": the zone is named with MSG-GEN-09.
    [Fact]
    public async Task Raft_added_to_a_discontinued_zone_is_msg_gen_09_naming_the_zone()
    {
        var owner = await factory.OwnerAsync();
        var zone = await owner.Client.CreateZoneAsync();
        await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/discontinue", new { rowVersion = zone.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var refused = await (await owner.Client.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody()))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09", "MSG01");

        Assert.Equal($"Farming zone {zone.Text("code")} is Discontinued and cannot be used for this action.", refused.Detail());
    }
}
