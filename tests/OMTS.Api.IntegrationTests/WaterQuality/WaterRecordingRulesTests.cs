using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 refusals: the values (BR-WATER-03), the raft In Use of the zone (BR-WATER-01), the required meter photo and
/// its ownership (BR-WATER-02), the zone In Use and the rights. Nothing is saved on a refusal.
/// </summary>
public class WaterRecordingRulesTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(14.01, 20.0, 25.0, "pH")]
    [InlineData(-0.5, 20.0, 25.0, "pH")]
    [InlineData(null, 20.0, 25.0, "pH")]
    [InlineData(7.0, 50.01, 25.0, "Salinity")]
    [InlineData(7.0, null, 25.0, "Salinity")]
    [InlineData(7.0, 20.0, 45.01, "Temperature")]
    [InlineData(7.0, 20.0, -1.0, "Temperature")]
    [InlineData(7.0, 20.0, null, "Temperature")]
    public async Task Invalid_or_missing_values_are_MSG01_and_nothing_is_saved(double? ph, double? salinity, double? temperature, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.RecordAsync((decimal?)ph, (decimal?)salinity, (decimal?)temperature);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal($"{field} is missing or invalid. Please check and enter it again.", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        await AssertNothingSavedAsync(z);
    }

    [Fact]
    public async Task Values_at_the_new_limits_are_saved_salinity_50_and_temperature_45()
    {
        var z = await api.ZoneAsync(thresholds: false);

        var body = await z.RecordedAsync(14m, 50m, temperature: 45m);

        Assert.Equal((14m, 50m, 45m), (body.GetProperty("ph").GetDecimal(), body.GetProperty("salinity").GetDecimal(), body.GetProperty("temperature").GetDecimal()));
    }

    [Fact]
    public async Task Future_time_and_discontinued_zone_are_MSG01_and_the_operation_id_is_optional()
    {
        var z = await api.ZoneAsync();
        var future = await z.RecordAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(10));
        var noTime = await z.Owner.PostAsJsonAsync(z.Base, new { raftId = z.Raft.Id, ph = 7m, salinity = 20m, temperature = 25m, photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id) });
        Assert.Equal("Measured at", (await future.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Measured at", (await noTime.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));

        var withoutOperation = await z.RecordAsync(7m, 20m, clientOperationId: null);
        Assert.Equal(HttpStatusCode.Created, withoutOperation.StatusCode);
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.ZoneId == z.Zone.Id));
        Assert.NotEqual(Guid.Empty, stored.ClientOperationId);

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);
        var discontinued = await z.RecordAsync(7m, 20m, photoKey: "water-meter-photos/x");
        var recognizeDiscontinued = await z.RecognizeAsync();

        Assert.Equal("Zone", (await discontinued.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(HttpStatusCode.BadRequest, recognizeDiscontinued.StatusCode);
        Assert.Equal("Zone", (await recognizeDiscontinued.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task The_raft_must_be_in_use_and_in_the_zone()
    {
        var z = await api.ZoneAsync();
        var otherZone = await api.ZoneAsync();
        var discontinued = await api.RaftAsync(z.Zone);
        await api.UpdateAsync<Raft>(discontinued.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        foreach (var response in new[]
                 {
                     await z.RecordAsync(7m, 20m, raftId: otherZone.Raft.Id),
                     await z.RecordAsync(7m, 20m, raftId: discontinued.Id),
                     await z.RecordAsync(7m, 20m, raftId: Guid.NewGuid()),
                     await z.Owner.PostAsJsonAsync(z.Base, new { measuredAt = DateTimeOffset.UtcNow, ph = 7m, salinity = 20m, temperature = 25m, photoKey = await z.Owner.PhotoKeyAsync(z.Zone.Id) }),
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal("Raft", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        await AssertNothingSavedAsync(z);
    }

    // WATER-01 PRE-2: a zone with no raft In Use has nowhere to measure, so the photo is not even stored.
    [Fact]
    public async Task A_zone_without_a_raft_in_use_refuses_the_recognition()
    {
        var z = await api.ZoneAsync();
        await api.UpdateAsync<Raft>(z.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        var response = await z.RecognizeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Raft", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
    }

    [Fact]
    public async Task The_meter_photo_is_required_and_only_the_key_of_the_caller_is_accepted()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var keyOfTheManager = await manager.PhotoKeyAsync(z.Zone.Id);
        var ownerPrefix = $"water-meter-photos/{z.OwnerAccount.Id:N}/";

        foreach (var (name, key) in new (string, string)[]
                 {
                     ("empty", ""),
                     ("of another account", keyOfTheManager),
                     ("never stored", $"{ownerPrefix}2026/11/{Guid.NewGuid():N}.png"),
                     ("path back to another account", $"{ownerPrefix}../{keyOfTheManager["water-meter-photos/".Length..]}"),
                     ("another folder", $"raft-photos/2026/11/{Guid.NewGuid():N}.png"),
                     ("too long", ownerPrefix + new string('a', 300)),
                 })
        {
            var response = await z.RecordAsync(7m, 20m, photoKey: key);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, name);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        var missing = await z.Owner.PostAsJsonAsync(z.Base, new { raftId = z.Raft.Id, measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1), ph = 7m, salinity = 20m, temperature = 25m });
        Assert.Equal("Photo", (await missing.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        await AssertNothingSavedAsync(z);
    }

    [Fact]
    public async Task A_photo_belongs_to_one_measurement()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m, photoKey: key)).StatusCode);

        var again = await z.RecordAsync(7.1m, 20m, photoKey: key);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Photo", (await again.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Staff_without_a_grant_in_the_zone_or_without_water_rights_is_refused_and_nothing_is_saved()
    {
        var z = await api.ZoneAsync();
        var (otherZone, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        // WATER-01 is No for the Accountant (§I.4.4).
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (revokedClient, revokedAccount) = await api.StaffAsync(z.Zone);
        var grant = await api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == revokedAccount.Id));
        await api.UpdateAsync<OMTS.Domain.Entities.Identity.AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);
        var trader = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

        foreach (var response in new[]
                 {
                     await z.RecordAsync(7m, 20m, client: otherZone),
                     await z.RecordAsync(7m, 20m, client: accountant),
                     await z.RecordAsync(7m, 20m, client: revokedClient),
                     await z.RecordAsync(7m, 20m, client: trader),
                     await z.RecognizeAsync(client: accountant),
                     await z.RecognizeAsync(client: otherZone),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        await AssertNothingSavedAsync(z);
    }

    private async Task AssertNothingSavedAsync(WaterZone z) =>
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
}
