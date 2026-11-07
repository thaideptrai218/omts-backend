using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01 and HARVEST-03 on SQL Server: what the database holds, and what reads back from it.</summary>
public class HarvestLotSqlServerStoreTests
{
    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        return (host, host.Owner.Client, (await host.Owner.Client.CreateZoneAsync()).Id());
    }

    private static async Task<(Guid RaftId, Guid CycleId)> SeededAsync(HttpClient owner, Guid zoneId, string? raftCode = null)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        return (raftId, cycleId);
    }

    [SqlServerFact]
    public async Task Stale_settings_answer_msg06_with_the_version_and_a_profile_save_makes_them_stale()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var read = await owner.HarvestSettingsAsync();
        Assert.Equal(8, Convert.FromBase64String(read.Text("rowVersion")).Length);

        var saved = await (await owner.PutHarvestSettingsAsync(60m, 4, read.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);

        var stale = await (await owner.PutHarvestSettingsAsync(70m, 4, read.Text("rowVersion"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", saved.GetProperty("settings").Text("rowVersion")), (stale.Text("code"), stale.Text("currentRowVersion")));
        Assert.Equal(60m, (await owner.HarvestSettingsAsync()).Kg("averageKgPerCage"));
    }

    [SqlServerFact]
    public async Task The_database_holds_the_code_unique_the_values_above_0_the_slots_the_settings_and_no_cascade()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] });
        var saved = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().Include(l => l.Lanes).SingleAsync(l => l.Id == lot.Id()));

        async Task RefusedAsync(string sql)
        {
            await using var connection = new SqlConnection(host.Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        }

        var columns = "Id, LotCode, RaftId, CycleId, HarvestedAt, HarvestDate, CageCount, KgPerCage, EstimatedKg, RecordedByAccountId, StatusId, CreatedAt";
        string Row(string code, int cages = 1, string kgPerCage = "50", string estimated = "50") =>
            $"INSERT INTO HarvestLots ({columns}) VALUES (NEWID(), N'{code}', '{saved.RaftId}', '{saved.CycleId}', SYSDATETIMEOFFSET(), '2026-10-26', {cages}, {kgPerCage}, {estimated}, '{saved.RecordedByAccountId}', 2901, SYSDATETIMEOFFSET())";
        await RefusedAsync(Row(saved.LotCode));
        await RefusedAsync(Row(saved.LotCode.ToLowerInvariant()));
        await RefusedAsync(Row("A-1", cages: 0));
        await RefusedAsync(Row("A-2", kgPerCage: "0"));
        await RefusedAsync(Row("A-3", estimated: "0"));
        await RefusedAsync($"UPDATE HarvestLots SET LeftoverKg = -1 WHERE Id = '{saved.Id}'");
        await RefusedAsync($"INSERT INTO HarvestLotPhotos (Id, LotId, SortOrder, FileKey, CreatedAt) VALUES (NEWID(), '{saved.Id}', 11, N'k', SYSDATETIMEOFFSET())");
        await RefusedAsync($"INSERT INTO HarvestLotPhotos (Id, LotId, SortOrder, FileKey, CreatedAt) VALUES (NEWID(), '{saved.Id}', 1, N'k', SYSDATETIMEOFFSET())");
        await RefusedAsync("UPDATE Cooperatives SET AverageKgPerCage = 0");
        await RefusedAsync("UPDATE Cooperatives SET DaysBeforeAutoClose = 0");
        await RefusedAsync($"DELETE FROM CultivationCycles WHERE Id = '{saved.CycleId}'");
        await RefusedAsync($"DELETE FROM Lanes WHERE Id = '{saved.Lanes.First().LaneId}'");
        await RefusedAsync($"DELETE FROM HarvestLots WHERE Id = '{saved.Id}'");
    }

    // decimal(18,3) and (9,2), read back as sent; the longest codes fit.
    [SqlServerFact]
    public async Task The_lot_reads_back_as_saved_on_every_route_with_the_longest_codes()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        await owner.SetHarvestSettingsAsync(12.35m, 3);
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody(new string('Z', 20)))).Id();
        var raft = await SeededAsync(owner, zoneId, new string('R', 20));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "3" });

        Assert.Equal((12.35m, 37.05m), (lot.Kg("kgPerCage"), lot.Kg("estimatedKg")));
        var again = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal((lot.Text("lotCode"), lot.Text("rowVersion")), (again.Text("lotCode"), again.Text("rowVersion")));
        Assert.Equal(lot.GetProperty("harvestedAt").GetDateTimeOffset(), again.GetProperty("harvestedAt").GetDateTimeOffset());
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(37.05m, screen.GetProperty("lots")[0].Kg("remainingKg"));
        Assert.Equal(
            await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.EstimatedKg).SingleAsync()),
            lot.Kg("estimatedKg"));
    }

    // What the lane states and the preview read translates to SQL.
    [SqlServerFact]
    public async Task The_lane_harvests_and_lane_progress_read_on_sql_server()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.Equal("Harvested", (await owner.ProgressAsync(raft.RaftId)).GetProperty("lanes")[0].Text("state"));
        Assert.Single((await owner.GetFromJsonAsync(raft.RaftId, 1)).GetProperty("harvestLots").EnumerateArray());
        Assert.Equal(1, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => (l.ReopenedOn ?? l.HarvestDate) <= Today && l.StatusId == 2901)));
        var preview = await (await owner.GetAsync("/api/harvest-settings/auto-close-preview?days=1")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, preview.GetProperty("lotCount").GetInt32());
    }
}

file static class LaneProgressCalls
{
    public static Task<JsonElement> GetFromJsonAsync(this HttpClient client, Guid raftId, int laneNumber) =>
        System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(client, $"/api/rafts/{raftId}/lanes/{laneNumber}/progress");
}
