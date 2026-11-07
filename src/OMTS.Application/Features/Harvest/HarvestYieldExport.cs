using OMTS.Application.Common.Interfaces.Reports;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-04.2: the figures shown, with the Not final marks, as an .xlsx file: the groups with a total row, and the lots of each group.</summary>
public static class HarvestYieldExport
{
    public const string GroupsSheet = "Groups";
    public const string LotsSheet = "Lots";
    public const string NotFinalMark = "Not final";

    public static readonly IReadOnlyList<string> GroupHeaders = ["Group", "Zone", "Lots", "Estimated kg", "Sold kg", "Lost kg", "Difference kg", "Not final"];

    public static readonly IReadOnlyList<string> LotHeaders =
        ["Group", "Lot code", "Zone", "Raft", "Harvest date", "Status", "Estimated kg", "Invoiced kg", "Returned kg", "Sold kg", "Lost kg", "Difference kg", "Not final"];

    public static IReadOnlyList<SpreadsheetSheet> Sheets(HarvestYieldReport report)
    {
        var groups = report.Groups.Select(g => (IReadOnlyList<object?>)
            [g.Label, g.ZoneCode, g.LotCount, g.EstimatedKg, g.SoldKg, g.LostKg, g.DifferenceKg, Mark(g.NotFinal)]).ToList();
        var t = report.Totals;
        groups.Add(["Total", null, t.LotCount, t.EstimatedKg, t.SoldKg, t.LostKg, t.DifferenceKg, Mark(t.NotFinal)]);
        var lots = report.Groups.SelectMany(g => g.Lots.Select(l => (IReadOnlyList<object?>)
        [
            g.Label, l.LotCode, l.ZoneCode, l.RaftCode, l.HarvestDate.ToDateTime(TimeOnly.MinValue), l.Status,
            l.EstimatedKg, l.InvoicedKg, l.ReturnedKg, l.SoldKg, l.LostKg, l.DifferenceKg, Mark(l.NotFinal),
        ])).ToList();
        return [new SpreadsheetSheet(GroupsSheet, GroupHeaders, groups), new SpreadsheetSheet(LotsSheet, LotHeaders, lots)];
    }

    /// <summary>yield-{raft|zone|cycle}-{from}-{to}.xlsx, dates as yyyyMMdd like the other exports.</summary>
    public static string FileName(HarvestYieldReport report) =>
        $"yield-{report.GroupBy.ToString().ToLowerInvariant()}-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.xlsx";

    private static string? Mark(bool notFinal) => notFinal ? NotFinalMark : null;
}
