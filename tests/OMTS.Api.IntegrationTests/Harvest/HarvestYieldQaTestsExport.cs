using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// QA of HARVEST-04.2 (the file holds the figures shown, with the Not final marks) and of HARVEST-04.0.E2 / MSG-GEN-01 (the fields to fix are pointed out).
/// </summary>
public class HarvestYieldQaTestsExport
{
    private static readonly DateOnly September = new(2026, 9, 1);
    private static readonly DateOnly EndOfSeptember = new(2026, 9, 30);

    // As the water and supplier exports: text a user typed (raft code) or that is built from it (lot code, group label) is never a formula; figures are number cells.
    [Fact]
    public async Task Text_starting_with_an_equals_sign_stays_text_and_the_figures_are_number_cells()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = (await owner.HarvestLotAsync(raft.RaftId)).Id();
        const string raftCode = "=HYPERLINK(\"http://evil.example\",\"Click\")";
        const string lotCode = "=SUM(1,2)";
        await api.UpdateAsync<Raft>(raft.RaftId, r => r.Code = raftCode);
        await api.UpdateAsync<HarvestLot>(lot, l => l.LotCode = lotCode);

        var file = await owner.GetYieldExportAsync(YieldFilters(groupBy: "raft"));

        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var group = book.Worksheet("Groups").Row(2);
        var row = book.Worksheet("Lots").Row(2);
        foreach (var (cell, text) in new[] { (group.Cell(1), raftCode), (row.Cell(1), raftCode), (row.Cell(2), lotCode), (row.Cell(4), raftCode) })
        {
            Assert.False(cell.HasFormula);
            Assert.Equal((XLDataType.Text, text), (cell.DataType, cell.GetString()));
        }

        foreach (var column in new[] { 3, 4, 5, 6, 7 })
        {
            Assert.Equal(XLDataType.Number, group.Cell(column).DataType);
            Assert.Equal(XLDataType.Number, book.Worksheet("Groups").Row(3).Cell(column).DataType);
        }

        Assert.Equal(400d, row.Cell(7).GetDouble());
    }

    // BR-HARVEST-07 + HARVEST-04.2: grouped by cycle the screen shows the lots outside the period with inPeriod = false; the file must keep that mark, or a reader cannot
    // tell why the lots of the file fall outside the period named in its file name.
    [Fact]
    public async Task The_cycle_export_marks_the_lots_harvested_outside_the_period()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var inPeriod = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1))).Id();
        var outside = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2))).Id();
        await api.DateLotAsync(inPeriod, September);
        await api.DateLotAsync(outside, new DateOnly(2026, 10, 5));
        var filters = YieldFilters(September, EndOfSeptember, "cycle");
        var screen = await owner.YieldAsync(filters);
        Assert.False(screen.YieldLot(outside).GetProperty("inPeriod").GetBoolean());

        var file = await owner.GetYieldExportAsync(filters);

        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var lots = book.Worksheet("Lots");
        var headers = lots.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("In period", headers);
        var column = headers.IndexOf("In period") + 1;
        var rows = lots.RowsUsed().Skip(1).ToList();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].Cell(column).GetString(), rows[1].Cell(column).GetString());
    }

    // HARVEST-04.1 lists each lot by its harvest date (a business date, BR-HARVEST-07): the file shows it as a date, not as a date at midnight.
    [Fact]
    public async Task The_harvest_date_is_exported_as_a_date_without_a_time_of_day()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = (await owner.HarvestLotAsync(raft.RaftId)).Id();
        await api.DateLotAsync(lot, new DateOnly(2026, 9, 2));

        var file = await owner.GetYieldExportAsync(YieldFilters(September, EndOfSeptember));

        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var cell = book.Worksheet("Lots").Row(2).Cell(5);
        Assert.Equal(new DateTime(2026, 9, 2), cell.GetDateTime());
        Assert.DoesNotContain("HH", cell.Style.DateFormat.Format);
        Assert.Equal("02/09/2026", cell.GetFormattedString());
    }

    // MSG-GEN-01: "the fields to fix are pointed out" (all of them), so a wrong grouping and a start after the end are both named in one answer.
    [Fact]
    public async Task A_wrong_grouping_and_a_start_after_the_end_are_both_pointed_out_in_one_answer()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var both = YieldFilters(new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), "week");

        var answer = await raft.Owner.Client.GetYieldAsync(both);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(new[] { "From date", "Grouping" }, (await answer.InvalidFieldsAsync()).Order());
    }
}
