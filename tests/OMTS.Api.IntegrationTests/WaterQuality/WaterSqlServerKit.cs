using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>A migrated SQL Server database with one zone (pH 6.5–8.5, salinity 15–30 ‰) and one raft, and how to record in it.</summary>
public sealed record WaterSqlServerZone(FarmingZone Zone, Raft Raft)
{
    /// <summary>Recognize, then the record request.</summary>
    public async Task<HttpResponseMessage> PostAsync(HttpClient client, decimal ph, decimal salinity, decimal temperature = 25m, Guid? operationId = null, object? measuredAt = null) =>
        await client.PostAsJsonAsync($"/api/zones/{Zone.Id}/water-measurements", new
        {
            clientOperationId = operationId,
            raftId = Raft.Id,
            measuredAt = measuredAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            ph,
            salinity,
            temperature,
            photoKey = await client.PhotoKeyAsync(Zone.Id),
        });

    public async Task<Guid> RecordAsync(HttpClient client, decimal ph, decimal salinity, decimal temperature = 25m, object? measuredAt = null)
    {
        var response = await PostAsync(client, ph, salinity, temperature, measuredAt: measuredAt);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Id();
    }
}

public static class WaterSqlServerKit
{
    public static async Task<WaterSqlServerZone> SeedAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var zone = new FarmingZone
        {
            Cooperative = new Cooperative { FullName = "HTX Trung Nam" },
            Code = NewCode(),
            Name = "Zone SQL",
            PhMin = 6.5m,
            PhMax = 8.5m,
            SalinityMin = 15m,
            SalinityMax = 30m,
        };
        var raft = new Raft { Zone = zone, Code = $"B-{NewCode()}" };
        db.Rafts.Add(raft);
        await db.SaveChangesAsync();
        return new WaterSqlServerZone(zone, raft);
    }
}
