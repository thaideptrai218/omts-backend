using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>The raft in the correction body (BR-WATER-01, BR-WATER-06): another raft In Use in the same zone, never another zone.</summary>
public class WaterCorrectionRaftTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_correction_moves_the_measurement_to_another_raft_in_use_of_the_zone_and_audits_it()
    {
        var z = await api.ZoneAsync();
        var other = await api.RaftAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);

        var body = await (await z.Owner.CorrectAsync(saved.Id(), 7m, 20m, raftId: other.Id)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((other.Id, other.Code), (body.GetProperty("raftId").GetGuid(), body.Text("raftCode")));
        Assert.Equal(other.Id, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == saved.Id()).Select(m => m.RaftId).SingleAsync()));
        var audit = Assert.Single(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Contains(z.Raft.Id.ToString(), audit.Before);
        Assert.Contains(other.Id.ToString(), audit.After);
    }

    [Fact]
    public async Task A_raft_missing_unknown_discontinued_or_of_another_zone_is_refused_on_the_raft_field()
    {
        var z = await api.ZoneAsync();
        var discontinued = await api.RaftAsync(z.Zone);
        await api.UpdateAsync<Raft>(discontinued.Id, r => r.StatusId = StatusIds.Usage.Discontinued);
        var otherZone = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);

        foreach (var raftId in new[] { discontinued.Id, otherZone.Raft.Id, Guid.NewGuid() })
        {
            await AssertRefusedOnRaftAsync(await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, raftId: raftId));
        }

        await AssertRefusedOnRaftAsync(await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/corrections", new
        {
            ph = 7.1m,
            salinity = 20m,
            temperature = 25m,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            reason = "Sai số",
        }));
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == saved.Id()));
        Assert.Equal((z.Raft.Id, 7m), (stored.RaftId, stored.Ph));
    }

    [Fact]
    public async Task Fields_are_named_in_form_order_with_the_raft_before_the_reason()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);

        var response = await z.Owner.CorrectAsync(saved.Id(), 15m, 51m, reason: null, temperature: null, measuredAt: DateTimeOffset.UtcNow.AddHours(1), raftId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Equal(["pH", "Salinity", "Temperature", "Measured at", "Raft", "Reason"], fields);
    }

    // The raft only records where the water was measured: a raft discontinued since then is not chosen again, it stays.
    [Fact]
    public async Task A_measurement_at_a_raft_discontinued_since_can_still_be_corrected_at_that_raft_or_moved_to_one_in_use()
    {
        var z = await api.ZoneAsync();
        var inUse = await api.RaftAsync(z.Zone);
        var first = await z.RecordedAsync(7m, 20m);
        var second = await z.RecordedAsync(7m, 20m);
        await api.UpdateAsync<Raft>(z.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        var stays = await z.Owner.CorrectAsync(first.Id(), 7.1m, 20m);
        var moves = await z.Owner.CorrectAsync(second.Id(), 7.1m, 20m, raftId: inUse.Id);
        var backToDiscontinued = await z.Owner.CorrectAsync(second.Id(), 7.1m, 20m, raftId: z.Raft.Id);
        var voided = await z.Owner.VoidAsync(first.Id());

        Assert.Equal(HttpStatusCode.OK, stays.StatusCode);
        Assert.Equal(z.Raft.Id, (await stays.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("raftId").GetGuid());
        Assert.Equal(inUse.Id, (await moves.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("raftId").GetGuid());
        await AssertRefusedOnRaftAsync(backToDiscontinued);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
    }

    private static async Task AssertRefusedOnRaftAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal("Raft", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }
}
