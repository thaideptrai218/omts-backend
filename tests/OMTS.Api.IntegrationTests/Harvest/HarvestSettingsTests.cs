using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-03, BR-HARVEST-06 and the copy of the average kg per cage onto each lot (BR-HARVEST-02).</summary>
public class HarvestSettingsTests
{
    private static async Task<JsonElement> PreviewAsync(HttpClient owner, int days) =>
        await (await owner.GetAsync($"/api/harvest-settings/auto-close-preview?days={days}")).ReadAsync(HttpStatusCode.OK);

    [Fact]
    public async Task A_new_cooperative_has_50_kg_and_3_days_and_saved_values_are_audited_before_and_after()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();

        var first = await owner.HarvestSettingsAsync();
        Assert.Equal((50m, 3), (first.Kg("averageKgPerCage"), first.GetProperty("daysBeforeAutoClose").GetInt32()));

        var saved = await (await owner.PutHarvestSettingsAsync(62.5m, 5, first.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Harvest settings saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        Assert.Equal((62.5m, 5), (saved.GetProperty("settings").Kg("averageKgPerCage"), saved.GetProperty("settings").GetProperty("daysBeforeAutoClose").GetInt32()));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Cooperative.HarvestSettingsChanged"));
        Assert.Equal(nameof(Cooperative), audit.RecordType);
        using var before = JsonDocument.Parse(audit.Before!);
        using var after = JsonDocument.Parse(audit.After!);
        Assert.Equal((50m, 3), (before.RootElement.Kg("AverageKgPerCage"), before.RootElement.GetProperty("DaysBeforeAutoClose").GetInt32()));
        Assert.Equal((62.5m, 5), (after.RootElement.Kg("AverageKgPerCage"), after.RootElement.GetProperty("DaysBeforeAutoClose").GetInt32()));

        // The same values again change nothing: no audit row.
        var again = await owner.HarvestSettingsAsync();
        (await owner.PutHarvestSettingsAsync(62.50m, 5, again.Text("rowVersion"))).EnsureSuccessStatusCode();
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Cooperative.HarvestSettingsChanged")));
    }

    public static TheoryData<string, decimal?, int?> Invalid => new()
    {
        { "Average kg per cage", null, 3 },
        { "Average kg per cage", 0m, 3 },
        { "Average kg per cage", -1m, 3 },
        { "Average kg per cage", 1.234m, 3 },
        { "Average kg per cage", 10_000_000m, 3 },
        { "Days before automatic close", 50m, null },
        { "Days before automatic close", 50m, 0 },
        { "Days before automatic close", 50m, -1 },
        { "Days before automatic close", 50m, 366 },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task An_invalid_value_is_named_and_changes_nothing(string field, decimal? average, int? days)
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();
        var current = await owner.HarvestSettingsAsync();

        var answer = await (await owner.PutHarvestSettingsAsync(average, days, current.Text("rowVersion"))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", field), (answer.Text("code"), answer.Text("field")));
        Assert.Equal(50m, (await owner.HarvestSettingsAsync()).Kg("averageKgPerCage"));
    }

    [Fact]
    public async Task A_whole_number_of_days_is_required_also_in_the_json_and_both_limits_are_saved()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();
        var current = await owner.HarvestSettingsAsync();

        var half = await owner.PutAsJsonAsync("/api/harvest-settings", new { averageKgPerCage = 50m, daysBeforeAutoClose = 1.5m, rowVersion = current.Text("rowVersion") });
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (half.StatusCode, await half.ErrorCodeAsync()));

        await owner.SetHarvestSettingsAsync(0.01m, 1);
        await owner.SetHarvestSettingsAsync(9_999_999.99m, 365);
        Assert.Equal(365, (await owner.HarvestSettingsAsync()).GetProperty("daysBeforeAutoClose").GetInt32());
    }

    [Fact]
    public async Task A_version_missing_or_unreadable_is_400()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();

        foreach (var version in new[] { null, "not-base64!" })
        {
            var answer = await (await owner.PutHarvestSettingsAsync(60m, 3, version)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "RowVersion"), (answer.Text("code"), answer.Text("field")));
        }
    }

    // BR-HARVEST-02: the copy is of the moment of the record, and a later setting does not touch it; a new average serves only later lots.
    [Fact]
    public async Task The_kg_per_cage_is_copied_at_record_time_and_a_later_setting_changes_no_lot()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var old = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "8" });

        await owner.SetHarvestSettingsAsync(62.5m, 3);

        var unchanged = await owner.GetHarvestLotAsync(old.Id());
        Assert.Equal((50m, 400m), (unchanged.Kg("kgPerCage"), unchanged.Kg("estimatedKg")));
        Assert.Equal(62.5m, (await owner.HarvestScreenAsync(raft.RaftId)).Kg("averageKgPerCage"));
        var next = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "8" });
        Assert.Equal((62.5m, 500m), (next.Kg("kgPerCage"), next.Kg("estimatedKg")));

        // An estimate sent by the manager keeps its own value and still copies the average.
        var sent = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "8", EstimatedKg = "123" });
        Assert.Equal((62.5m, 123m), (sent.Kg("kgPerCage"), sent.Kg("estimatedKg")));
    }

    // GFR-05: with 3 days a lot harvested on 26/10 is closed at the check at the start of 30/10; "the next check" of today 29/10 is that one.
    [Fact]
    public async Task The_preview_counts_what_the_next_check_closes_from_the_harvest_or_reopen_day_and_asks_only_when_days_are_lowered()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var today = Today;
        var ofDays = new Dictionary<int, Guid>();
        foreach (var age in new[] { 3, 2, 1, 0 })
        {
            var lot = await owner.HarvestLotAsync(raft.RaftId);
            await factory.UpdateAsync<HarvestLot>(lot.Id(), l => l.HarvestDate = today.AddDays(-age));
            ofDays[age] = lot.Id();
        }

        var same = await PreviewAsync(owner, 3);
        Assert.Equal((3, 1, false), (same.GetProperty("days").GetInt32(), same.GetProperty("lotCount").GetInt32(), same.GetProperty("lowered").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, same.GetProperty("message").ValueKind);

        var lowered = await PreviewAsync(owner, 1);
        Assert.Equal((3, true), (lowered.GetProperty("lotCount").GetInt32(), lowered.GetProperty("lowered").GetBoolean()));
        Assert.Equal(("With 1 days, the next daily close check will close 3 lots that are still for sale. Do you want to save?", "MSG-HARVEST-03"), (lowered.Text("message"), lowered.Text("messageCode")));

        // A Closed lot does not count; a Reopened one counts from the day it was reopened.
        await factory.UpdateAsync<HarvestLot>(ofDays[1], l => { l.StatusId = StatusIds.HarvestLot.Closed; l.CloseDate = today; });
        await factory.UpdateAsync<HarvestLot>(ofDays[2], l => { l.StatusId = StatusIds.HarvestLot.Reopened; l.ReopenedOn = today; });
        Assert.Equal(1, (await PreviewAsync(owner, 1)).GetProperty("lotCount").GetInt32());
    }

    [Theory]
    [InlineData("?days=0")]
    [InlineData("?days=366")]
    [InlineData("?days=-1")]
    [InlineData("")]
    [InlineData("?days=abc")]
    [InlineData("?days=1.5")]
    public async Task The_preview_refuses_days_that_cannot_be_saved(string query)
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();

        var answer = await owner.GetAsync($"/api/harvest-settings/auto-close-preview{query}");

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (answer.StatusCode, await answer.ErrorCodeAsync()));
    }

    // SCR-HARVEST-02 is kept apart from the profile, whose form does not send or reset them.
    [Fact]
    public async Task The_profile_neither_shows_nor_resets_the_settings()
    {
        using var factory = new AuthApiFactory();
        var owner = (await factory.OwnerAsync()).Client;
        await owner.CreateZoneAsync();
        await owner.SetHarvestSettingsAsync(70m, 7);

        var profile = await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile");

        Assert.DoesNotContain("KgPerCage", profile.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(70m, (await owner.HarvestSettingsAsync()).Kg("averageKgPerCage"));
    }
}
