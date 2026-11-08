using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-04 filters: the period by harvest date, the cycle grouping that pulls a whole cycle, the wrong period and grouping (MSG-GEN-01), an empty selection (MSG-GEN-10), and the export.</summary>
public class HarvestYieldFilterTests
{
    private static readonly DateOnly September = new(2026, 9, 1);

    [Fact]
    public async Task A_lot_is_in_the_period_by_its_harvest_date_both_ends_included_and_the_default_is_the_current_month()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var last = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var before = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        var today = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(4));
        await api.DateLotAsync(first.Id(), September);
        await api.DateLotAsync(last.Id(), new DateOnly(2026, 9, 30));
        await api.DateLotAsync(before.Id(), new DateOnly(2026, 8, 31));

        var report = await owner.YieldAsync(YieldFilters(September, new DateOnly(2026, 9, 30)));

        Assert.Equal(new[] { first.Id(), last.Id() }.Order(), report.YieldLotIds().Order());
        Assert.Equal((September, new DateOnly(2026, 9, 30), "Raft"), (report.Date("from"), report.Date("to"), report.Text("groupBy")));
        var current = await owner.YieldAsync();
        Assert.Equal([today.Id()], current.YieldLotIds());
        var month = new DateOnly(FarmingTestKit.Today.Year, FarmingTestKit.Today.Month, 1);
        Assert.Equal((month, month.AddMonths(1).AddDays(-1)), (current.Date("from"), current.Date("to")));
        Assert.Equal([today.Id()], (await owner.YieldAsync(YieldFilters(to: FarmingTestKit.Today))).YieldLotIds());
    }

    // BR-HARVEST-07: grouped by cycle, the report shows all the lots of each cycle that has a lot in the period.
    [Fact]
    public async Task Grouped_by_cycle_every_lot_of_a_cycle_with_a_lot_in_the_period_is_shown()
    {
        using var api = new AuthApiFactory();
        var raftA = await api.SeededRaftAsync();
        var owner = raftA.Owner.Client;
        var raftB = (await owner.CreateRaftAsync(raftA.ZoneId)).Id();
        var cycleB = (await owner.StartCycleAsync(raftB, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleB, raftA.Lot.Session([1, 2]));
        var inPeriod = await owner.HarvestLotAsync(raftA.RaftId, HarvestFields.OfLanes(1));
        var later = await owner.HarvestLotAsync(raftA.RaftId, new HarvestFields { Lanes = ["2"], Cages = "3" });
        var otherCycle = await owner.HarvestLotAsync(raftB, HarvestFields.OfLanes(1));
        await api.DateLotAsync(inPeriod.Id(), September);
        await api.DateLotAsync(later.Id(), new DateOnly(2026, 10, 15));
        await api.DateLotAsync(otherCycle.Id(), new DateOnly(2026, 10, 16));
        var september = YieldFilters(September, new DateOnly(2026, 9, 30), "cycle");

        var byCycle = await owner.YieldAsync(september);
        var byRaft = await owner.YieldAsync(YieldFilters(September, new DateOnly(2026, 9, 30), "RAFT"));

        var group = Assert.Single(byCycle.Groups());
        Assert.Equal((raftA.CycleId, 2, 550m), (group.GetProperty("key").GetGuid(), group.GetProperty("lotCount").GetInt32(), group.Kg("estimatedKg")));
        Assert.Equal([true, false], group.GetProperty("lots").EnumerateArray().Select(l => l.GetProperty("inPeriod").GetBoolean()));
        Assert.Equal((2, 550m), (byCycle.GetProperty("totals").GetProperty("lotCount").GetInt32(), byCycle.GetProperty("totals").Kg("estimatedKg")));
        Assert.Equal([inPeriod.Id()], byRaft.YieldLotIds());
    }

    [Fact]
    public async Task A_start_after_the_end_or_an_unknown_grouping_answers_msg_gen_01_and_gives_no_file()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var backwards = YieldFilters(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1));

        Assert.Equal(["From date"], await (await owner.GetYieldAsync(backwards)).InvalidFieldsAsync());
        Assert.Equal(["From date"], await (await owner.GetYieldExportAsync(backwards)).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetYieldAsync(YieldFilters(groupBy: "week"))).InvalidFieldsAsync());
        Assert.Equal(["Grouping"], await (await owner.GetYieldAsync(YieldFilters(groupBy: "1"))).InvalidFieldsAsync());
        Assert.Equal(["To date"], await (await owner.GetYieldAsync(YieldFilters(new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 2)))).InvalidFieldsAsync());
        Assert.Equal("Zone", (await owner.YieldAsync(YieldFilters(groupBy: " Zone "))).Text("groupBy"));
    }

    // HARVEST-04.0.E1: no lot in the period and zones chosen.
    [Fact]
    public async Task An_empty_period_answers_200_with_msg_gen_10_and_zero_totals()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var manager = (await api.ManagerAsync(raft.ZoneId)).Client;

        var report = await manager.YieldAsync(YieldFilters(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), zoneIds: [raft.ZoneId]));

        Assert.Empty(report.Groups());
        Assert.Equal((0m, 0m, 0m, 0m, false), report.GetProperty("totals").Figures());
        Assert.Equal(0, report.GetProperty("totals").GetProperty("lotCount").GetInt32());
        Assert.Equal(("MSG-GEN-10", "There is no data for the period and zones you chose. Please choose again."), (report.Text("messageCode"), report.Text("message")));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, report.GetProperty("notFinalNotice").ValueKind);
    }

    // HARVEST-04.2: the figures shown, with the Not final marks, in a file.
    [Fact]
    public async Task The_export_holds_the_groups_with_a_total_row_and_the_lots_with_the_not_final_marks()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var open = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var closed = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["2"], Cages = "2" });
        await api.CloseLotAsync(owner, closed.Id(), 40m, "Spoiled");
        await api.DateLotAsync(open.Id(), September);
        await api.DateLotAsync(closed.Id(), new DateOnly(2026, 9, 2));
        var manager = (await api.ManagerAsync(raft.ZoneId)).Client;

        var answer = await manager.GetYieldExportAsync(YieldFilters(September, new DateOnly(2026, 9, 30), "raft"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", answer.Content.Headers.ContentType!.MediaType);
        Assert.Equal("yield-raft-20260901-20260930.xlsx", answer.Content.Headers.ContentDisposition!.FileName);
        using var book = new XLWorkbook(await answer.Content.ReadAsStreamAsync());
        Assert.Equal(["Groups", "Lots"], book.Worksheets.Select(s => s.Name));
        var groups = book.Worksheet("Groups").RowsUsed().ToList();
        Assert.Equal(["Group", "Zone", "Lots", "Estimated kg", "Sold kg", "Lost kg", "Difference kg", "Not final"], groups[0].Cells(1, 8).Select(c => c.GetString()));
        Assert.Equal(open.Text("raftCode"), groups[1].Cell(1).GetString());
        Assert.Equal((2m, 500m, 0m, 40m, 460m, "Not final"), Numbers(groups[1]));
        Assert.Equal(("Total", (2m, 500m, 0m, 40m, 460m, "Not final")), (groups[2].Cell(1).GetString(), Numbers(groups[2])));
        var lots = book.Worksheet("Lots").RowsUsed().Skip(1).ToList();
        Assert.Equal([open.Text("lotCode"), closed.Text("lotCode")], lots.Select(r => r.Cell(2).GetString()));
        Assert.Equal(("Open", "Not final"), (lots[0].Cell(6).GetString(), lots[0].Cell(13).GetString()));
        Assert.Equal(("Closed", 40m, string.Empty), (lots[1].Cell(6).GetString(), lots[1].Cell(11).GetValue<decimal>(), lots[1].Cell(13).GetString()));
        Assert.Equal(new DateTime(2026, 9, 2), lots[1].Cell(5).GetDateTime());

        var empty = await manager.GetYieldExportAsync(YieldFilters(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        using var emptyBook = new XLWorkbook(await empty.Content.ReadAsStreamAsync());
        var emptyRows = emptyBook.Worksheet("Groups").RowsUsed().Skip(1).ToList();
        Assert.Equal(("Total", 0m), (Assert.Single(emptyRows).Cell(1).GetString(), emptyRows[0].Cell(4).GetValue<decimal>()));
        Assert.Single(emptyBook.Worksheet("Lots").RowsUsed());
    }

    private static (decimal Lots, decimal Estimated, decimal Sold, decimal Lost, decimal Difference, string NotFinal) Numbers(IXLRow row) =>
        (row.Cell(3).GetValue<decimal>(), row.Cell(4).GetValue<decimal>(), row.Cell(5).GetValue<decimal>(), row.Cell(6).GetValue<decimal>(), row.Cell(7).GetValue<decimal>(),
            row.Cell(8).GetString());
}
