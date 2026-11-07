using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-03 access per §I.4.4, and the detail of WATER-03.2.</summary>
public class WaterHistoryAccessTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    // Full for the Owner, Full* for a Zone Manager in the zones of their pairs, No for the others.
    [Fact]
    public async Task Zone_managers_of_the_zone_and_the_owner_read_it_others_get_MSG_GEN_08()
    {
        var z = await api.ZoneAsync();
        var measurement = await z.RecordedAsync(7m, 20m);
        var (otherZoneManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (manager, _) = await api.StaffAsync(z.Zone);
        var urls = new[] { z.Base, $"{z.Base}/chart", $"{z.Base}/export", $"/api/water-measurements/{measurement.Id()}" };

        foreach (var url in urls)
        {
            foreach (var refused in new[] { otherZoneManager, accountant })
            {
                var response = await refused.GetAsync(url);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }

            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await z.Owner.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Detail_has_the_thresholds_of_the_measurement_and_a_short_lived_photo_url()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id, "ph7.7_sal23");
        var id = (await (await z.RecordAsync(7.5m, 23m, photoKey: key)).Content.ReadFromJsonAsync<JsonElement>()).Id();

        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{id}");
        var photo = await z.Owner.GetAsync(detail.Text("photoUrl"));

        Assert.Equal(7.5m, detail.GetProperty("ph").GetDecimal());
        Assert.Equal(6.5m, detail.GetProperty("thresholdsUsed").GetProperty("phMin").GetDecimal());
        Assert.Equal(10m, detail.GetProperty("thresholdsUsed").GetProperty("temperatureMin").GetDecimal());
        Assert.Equal((z.Zone.Code, z.Raft.Code), (detail.Text("zoneCode"), detail.Text("raftCode")));
        Assert.False(detail.TryGetProperty("ocrDraft", out _));
        Assert.False(detail.TryGetProperty("recordVersion", out _));
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(WaterTestKit.Photo("ph7.7_sal23"), await photo.Content.ReadAsByteArrayAsync());
    }
}
