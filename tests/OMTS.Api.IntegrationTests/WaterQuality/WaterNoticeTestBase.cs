using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02.2 seen by a Zone Manager of the zone while the Owner acts (the Owner is not told of their own action); the zone
/// has thresholds pH 6.5–8.5, salinity 15–30 ‰, temperature 10–35 °C.
/// </summary>
public abstract class WaterNoticeTestBase(AuthApiFactory api)
{
    protected static readonly DateTimeOffset Older = DateTimeOffset.UtcNow.AddHours(-6);
    protected static readonly DateTimeOffset Latest = DateTimeOffset.UtcNow.AddHours(-3);
    protected static readonly DateTimeOffset MovedLater = DateTimeOffset.UtcNow.AddHours(-1);
    protected static readonly DateTimeOffset MovedEarlier = DateTimeOffset.UtcNow.AddHours(-9);

    protected AuthApiFactory Api => api;

    protected async Task<(WaterZone Z, Account Manager)> ZoneAsync()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        return (z, manager);
    }

    protected static async Task<Guid> RecordAsync(WaterZone z, decimal ph, decimal salinity, DateTimeOffset measuredAt) =>
        (await z.RecordedAsync(ph, salinity, measuredAt: measuredAt)).Id();

    protected static async Task<JsonElement> CorrectedAsync(
        WaterZone z, Guid id, decimal ph, decimal salinity, DateTimeOffset measuredAt, string reason = "Sai số", Guid? raftId = null)
    {
        var response = await z.Owner.CorrectAsync(id, ph, salinity, reason: reason, measuredAt: measuredAt, raftId: raftId ?? z.Raft.Id);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected async Task<List<Notification>> ManagerAlertsAsync(Guid id, Account manager) =>
        (await api.AlertsOfAsync(id)).Where(n => n.RecipientAccountId == manager.Id).ToList();
}
