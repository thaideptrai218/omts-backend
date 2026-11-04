using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Independent QA of FARM-04 on SQL Server: parallel saves with real row versions, and more of the migration.</summary>
public class RaftsV2QaTestsSqlServer
{
    private const string MigrationBefore = "20261104100119_PurchaseReturnsV2";

    private static async Task<string> Show(HttpResponseMessage[] answers)
    {
        var lines = new List<string>();
        foreach (var answer in answers)
        {
            lines.Add($"{(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
        }

        return string.Join(" | ", lines);
    }

    // FARM-04.0.E1 and GBR-03: a duplicate code answers MSG-GEN-02 with the existing raft, also for a save stopped in parallel. CreateAsync
    // saves the zone row too (MarkChanged, against a parallel discontinue), so the losers may fail on its row version before the unique index.
    [SqlServerFact]
    public async Task Parallel_creates_of_one_code_answer_msg_gen_02_with_the_existing_raft()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync();
        var proposal = (await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}/rafts/next-code")).Text("code");

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody(i % 2 == 0 ? proposal : proposal.ToLowerInvariant()))));

        var shown = await Show(answers);
        var saved = await host.QueryAsync(db => db.Rafts.Where(r => r.ZoneId == zone.Id()).ToListAsync());
        var winner = Assert.Single(saved);
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
        foreach (var lost in answers.Where(a => a.StatusCode != HttpStatusCode.Created))
        {
            var body = await lost.ReadAsync(HttpStatusCode.Conflict);
            Assert.True(body.Text("code") == "MSG-GEN-02", shown);
            Assert.Equal(winner.Id, body.GetProperty("existingId").GetGuid());
        }
    }

    // A lost zone race with no raft of that code keeps MSG06: there is no record to offer, and whatever answered 201 is saved.
    [SqlServerFact]
    public async Task A_parallel_create_that_loses_the_zone_race_with_a_free_code_answers_msg06()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody())));

        var shown = await Show(answers);
        var created = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
        Assert.True(created >= 1, shown);
        Assert.Equal(created, await host.QueryAsync(db => db.Rafts.CountAsync(r => r.ZoneId == zone.Id())));
        foreach (var lost in answers.Where(a => a.StatusCode != HttpStatusCode.Created))
        {
            var body = await lost.ReadAsync(HttpStatusCode.Conflict);
            Assert.True(body.Text("code") == "MSG06" && !body.TryGetProperty("existingId", out _), shown);
        }
    }

    // Accepted (L-2). Two rafts with different codes added to one zone at the same time: one loses with MSG06, because each save rewrites the
    // zone row to guard against a parallel discontinue.
    [SqlServerFact(Skip = "Accepted: adding a raft rewrites the zone row to guard against a parallel discontinue; the loser reloads (MSG06).")]
    public async Task Parallel_creates_of_different_codes_all_save()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = await owner.CreateZoneAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody())));

        var shown = await Show(answers);
        Assert.True(answers.All(a => a.StatusCode == HttpStatusCode.Created), shown);
    }

    // Accepted (L-2, same cause). Adding a raft moves the row version and UpdatedAt of the zone, so a zone edit form open at that moment answers
    // MSG06 "just updated by another user" though no zone field changed. The client list tells the form to reload.
    [SqlServerFact(Skip = "Accepted: adding a raft rewrites the zone row to guard against a parallel discontinue; the loser reloads (MSG06).")]
    public async Task Adding_a_raft_leaves_the_open_zone_form_valid()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewZoneBody();
        var zone = await owner.CreateZoneAsync(body);

        await owner.CreateRaftAsync(zone.Id());

        var saved = await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Name = "Renamed", RowVersion = zone.Text("rowVersion") });
        await saved.ReadAsync(HttpStatusCode.OK);
    }

    // Latitude and longitude are stored with 6 decimals and a map picker sends more: the value is rounded to what the column stores before it is
    // compared and saved, so every answer shows the stored value and a repeat save changes nothing (GBR-02: the log holds real changes).
    [SqlServerFact]
    public async Task A_raft_pin_with_more_than_six_decimals_is_rounded_as_stored_and_saving_it_again_changes_nothing()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody() with { Latitude = 21.123456789m, Longitude = 107.123456789m };
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);

        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
        foreach (var answer in new[] { raft, read })
        {
            Assert.Equal((21.123457m, 107.123457m), (answer.GetProperty("latitude").GetDecimal(), answer.GetProperty("longitude").GetDecimal()));
        }

        var same = await owner.EditRaftAsync(raft.Id(), body with { RowVersion = read.Text("rowVersion") });

        Assert.Equal(read.Text("rowVersion"), same.Text("rowVersion"));
        Assert.Equal(21.123457m, same.GetProperty("latitude").GetDecimal());
        Assert.Empty(await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == raft.Id() && e.Action == "Raft.Updated").ToListAsync()));
    }

    // The zone shares the check and the column type. The range is checked as sent: just past the edge is refused, just below it rounds to the edge.
    [SqlServerFact]
    public async Task A_zone_pin_is_rounded_as_stored_and_only_a_value_past_the_edge_is_refused()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewZoneBody() with { Latitude = 89.9999999m, Longitude = -179.9999999m };
        var zone = await owner.CreateZoneAsync(body);
        Assert.Equal((90m, -180m), (zone.GetProperty("latitude").GetDecimal(), zone.GetProperty("longitude").GetDecimal()));

        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/zones/{zone.Id()}");
        var same = await owner.EditZoneAsync(zone.Id(), body with { RowVersion = read.Text("rowVersion") });
        Assert.Equal(read.Text("rowVersion"), same.Text("rowVersion"));
        Assert.Empty(await host.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == zone.Id() && e.Action == "FarmingZone.Updated").ToListAsync()));

        Assert.Equal(["Latitude"], await (await owner.PutAsJsonAsync($"/api/zones/{zone.Id()}", body with { Latitude = 90.0000001m, RowVersion = read.Text("rowVersion") })).InvalidFieldsAsync());
    }

    // The status catalogue as migrated: LANE is 2001 and 2002, transitions 76 and 77 are the only ones of the group, Owner only.
    [SqlServerFact]
    public async Task The_lane_statuses_and_transitions_are_exactly_the_agreed_ids()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();

        var statuses = await host.QueryAsync(db => db.Statuses.AsNoTracking().Where(s => s.Group == StatusGroups.Lane).OrderBy(s => s.Id).ToListAsync());
        Assert.Equal([(2001, "IN_USE", "In Use", true), (2002, "REMOVED", "Removed", false)], statuses.Select(s => (s.Id, s.Code, s.DisplayName, s.IsInitial)));
        var transitions = await host.QueryAsync(db => db.StatusTransitions.AsNoTracking()
            .Where(t => t.FromStatusId == 2001 || t.FromStatusId == 2002 || t.ToStatusId == 2001 || t.ToStatusId == 2002).OrderBy(t => t.Id).ToListAsync());
        Assert.Equal([(76, 2001, 2002), (77, 2002, 2001)], transitions.Select(t => (t.Id, t.FromStatusId, t.ToStatusId)));
        Assert.All(transitions, t => Assert.Equal("Cooperative Owner", t.AllowedPerformers));
        Assert.Equal(2, await host.QueryAsync(db => db.Statuses.CountAsync(s => s.Id == 2001 || s.Id == 2002)));
        Assert.Equal(1, await host.QueryAsync(db => db.StatusTransitions.CountAsync(t => t.Id == 76)));
    }

    // Each answer is saved or a clean refusal, never a 500, whatever else happens to the raft at the same moment.
    [SqlServerFact]
    public async Task Mixed_parallel_changes_of_one_raft_never_answer_500_and_files_match_rows()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 4);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        await owner.AddRaftPhotoAsync(raft.Id());
        var current = await owner.AddRaftPhotoAsync(raft.Id());

        for (var round = 0; round < 4; round++)
        {
            var photoId = current.GetProperty("photos")[0].Id();
            var answers = await Task.WhenAll(
                owner.UploadRaftPhotoAsync(raft.Id()),
                owner.UploadRaftPhotoAsync(raft.Id()),
                owner.DeleteAsync($"/api/rafts/{raft.Id()}/photos/{photoId}"),
                owner.DeleteAsync($"/api/rafts/{raft.Id()}/photos/{photoId}"),
                owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 1 + round, RowVersion = current.Text("rowVersion") }),
                owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 6 + round, RowVersion = current.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);
            Assert.True(answers.All(a => a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.BadRequest), shown);

            current = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
            var slots = current.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("sortOrder").GetInt32()).ToList();
            Assert.InRange(slots.Count, 0, Raft.MaxPhotos);
            Assert.Equal(slots.Distinct(), slots);
            Assert.Equal((await host.QueryAsync(db => db.RaftPhotos.Where(p => p.RaftId == raft.Id()).Select(p => p.FileKey).ToListAsync())).Order(), host.StoredKeys("raft-photos"));
            var numbers = current.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()).ToList();
            Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);

            if (slots.Count == 0)
            {
                current = await owner.AddRaftPhotoAsync(raft.Id());
            }
        }
    }

    [SqlServerFact]
    public async Task A_discontinue_racing_uploads_and_edits_leaves_no_write_after_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 2);
        var zoneId = (await owner.CreateZoneAsync()).Id();

        for (var round = 0; round < 4; round++)
        {
            var raft = await owner.CreateRaftAsync(zoneId, body with { Code = NewCode() });
            var answers = await Task.WhenAll(
                owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") }),
                owner.UploadRaftPhotoAsync(raft.Id()),
                owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { Code = raft.Text("code"), LaneCount = 5, RowVersion = raft.Text("rowVersion") }));

            var shown = await Show(answers);
            Assert.True(answers.All(a => (int)a.StatusCode < 500), shown);

            var after = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");
            var stopped = answers[0].StatusCode == HttpStatusCode.OK;
            var grew = answers[2].StatusCode == HttpStatusCode.OK;
            Assert.False(stopped && grew, shown);
            Assert.Equal(stopped ? StatusIds.Usage.Discontinued : StatusIds.Usage.InUse, after.GetProperty("statusId").GetInt32());
            Assert.Equal(grew ? 5 : 2, after.GetProperty("laneCount").GetInt32());
            Assert.Equal(answers[1].StatusCode == HttpStatusCode.OK ? 1 : 0, after.GetProperty("photos").GetArrayLength());
        }
    }

    // Real row versions: a blocked lane change writes nothing, a photo moves the version, the old version is then stale.
    [SqlServerFact]
    public async Task A_blocked_lane_change_keeps_the_row_version_and_a_photo_moves_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var body = NewRaftBody(lanes: 2);
        var raft = await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), body);
        var cycle = new OMTS.Domain.Entities.Cultivation.CultivationCycle { RaftId = raft.Id(), StartDate = Today, StatusId = StatusIds.CultivationCycle.Open };
        await host.UseDbAsync(db => db.CultivationCycles.Add(cycle));

        var blocked = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { LaneCount = 3, RowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(cycle.Id, blocked.GetProperty("cycleId").GetGuid());
        Assert.Equal(raft.Text("rowVersion"), (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}")).Text("rowVersion"));

        var withPhoto = await owner.AddRaftPhotoAsync(raft.Id());
        Assert.NotEqual(raft.Text("rowVersion"), withPhoto.Text("rowVersion"));
        Assert.Equal("MSG06", await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", body with { RowVersion = raft.Text("rowVersion") })).ErrorCodeAsync());
        await owner.EditRaftAsync(raft.Id(), body with { RowVersion = withPhoto.Text("rowVersion") });
    }

    // Down keeps the lowest slot, also when slot 1 is free; a blank PhotoUrl makes no photo; SeedingEvents stays (PurchaseReceiptsV2 dropped SeedReceiptLots before it); the model has no pending change.
    [SqlServerFact]
    public async Task The_migration_keeps_the_lowest_slot_ignores_blanks_and_leaves_seed_tables_alone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX" }, Code = "KV01", Name = "A" };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        var (spaces, secondOnly) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO Rafts (Id, ZoneId, Code, PhotoUrl, StatusId, CreatedAt)
            VALUES ({spaces}, {zone.Id}, N'B01', N'   ', 101, SYSDATETIMEOFFSET()), ({secondOnly}, {zone.Id}, N'B02', NULL, 102, SYSDATETIMEOFFSET());
            INSERT INTO Lanes (Id, RaftId, LaneNumber, CreatedAt) VALUES (NEWID(), {secondOnly}, 1, SYSDATETIMEOFFSET());
            """);

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.RaftPhotos.AsNoTracking().ToListAsync());
        Assert.Equal(StatusIds.Lane.InUse, await db.Lanes.AsNoTracking().Select(l => l.StatusId).SingleAsync());
        Assert.Equal(StatusIds.Usage.Discontinued, await db.Rafts.AsNoTracking().Where(r => r.Id == secondOnly).Select(r => r.StatusId).SingleAsync());
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'SeedingEvents'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_RaftPhotos_RaftId_SortOrder' AND is_unique = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_RaftPhoto_SortOrder'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_Lanes_Statuses_StatusId'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_Lanes_RaftId_LaneNumber' AND is_unique = 1"));

        db.RaftPhotos.Add(new RaftPhoto { RaftId = secondOnly, SortOrder = 2, FileKey = "raft-photos/2026/01/slot2.jpg" });
        db.RaftPhotos.Add(new RaftPhoto { RaftId = secondOnly, SortOrder = 3, FileKey = "raft-photos/2026/01/slot3.jpg" });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal("raft-photos/2026/01/slot2.jpg", await db.Database.SqlQuery<string>($"SELECT PhotoUrl AS [Value] FROM Rafts WHERE Id = {secondOnly}").SingleAsync());

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var photo = await db.RaftPhotos.AsNoTracking().SingleAsync();
        Assert.Equal((secondOnly, 1, "raft-photos/2026/01/slot2.jpg"), (photo.RaftId, photo.SortOrder, photo.FileKey));
    }

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
