using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-04 – farming yield by raft, zone or cycle over a period of harvest dates, worked out when it is shown and never stored (BR-HARVEST-07); viewing and exporting change no data.
/// §I.4.4: the Owner for any zone or all zones together, a Zone Manager in the zones of their pairs (BR-FARM-08), not an Accountant: the right is HARVEST_LOTS View, and Export for the file.
/// The sold kg come from the sales port (<see cref="IHarvestLotSalesQuery"/>), which subtracts nothing returned until CLAIM-03 fills its returned, back-for-sale and discarded kg.
/// </summary>
public sealed class HarvestYieldService(
    IHarvestLotRepository lots, IHarvestLotSalesQuery sales, ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    public const string GroupingField = "Grouping";

    public Task<Result<HarvestYieldReport>> ReportAsync(HarvestYieldQuery query, CancellationToken cancellationToken) =>
        BuildAsync(query, PermissionAction.View, cancellationToken);

    /// <summary>HARVEST-04.2: the same report as a file; an empty selection gives the headers and a zero total row, as the other exports.</summary>
    public async Task<Result<ExportedFile>> ExportAsync(HarvestYieldQuery query, CancellationToken cancellationToken)
    {
        var report = await BuildAsync(query, PermissionAction.Export, cancellationToken);
        if (!report.IsSuccess)
        {
            return report.Error!;
        }

        try
        {
            return new ExportedFile(HarvestYieldExport.FileName(report.Value!), ISpreadsheetWriter.ContentType, spreadsheets.Write(HarvestYieldExport.Sheets(report.Value!)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private async Task<Result<HarvestYieldReport>> BuildAsync(HarvestYieldQuery query, PermissionAction action, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.HarvestLots, action, cancellationToken);
        var chosen = (query.ZoneIds ?? []).Distinct().ToList();
        if (zones.IsEmpty || !chosen.TrueForAll(zones.Allows))
        {
            return AppError.Forbidden;
        }

        var grouping = ParseGrouping(query.GroupBy);
        var period = PurchasePeriod.Resolve(query.From, query.To, timeProvider.Today());
        // MSG-GEN-01 points out every field to fix: a wrong grouping and a wrong period together are named in one answer.
        if (grouping is null && !period.IsSuccess && period.Error!.Details?.GetValueOrDefault("field") is string periodField)
        {
            return AppError.InvalidFields([GroupingField, periodField]);
        }

        if (grouping is not { } groupBy)
        {
            return AppError.Invalid(GroupingField, "Use raft, zone or cycle.");
        }

        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var (from, to) = period.Value;
        IReadOnlyCollection<Guid>? zoneIds = chosen.Count > 0 ? chosen : zones.AllZones ? null : zones.ZoneIds;
        var selected = await lots.ListForYieldAsync(zoneIds, from, to, groupBy == HarvestYieldGrouping.Cycle, cancellationToken);
        var figures = await sales.FiguresAsync(selected.Select(l => l.Id).ToList(), cancellationToken);
        var result = HarvestYieldCalculator.Build(selected, figures, groupBy, from, to, statusCatalog.DisplayNameOf);
        var notFinal = result.Totals.NotFinal ? UserMessages.YieldNotFinal : null;
        var empty = selected.Count == 0 ? UserMessages.NoDataForFilters("period and zones") : null;
        return new HarvestYieldReport(from, to, groupBy, result.Groups, result.Totals, notFinal?.Text, notFinal?.Code, empty?.Text, empty?.Code);
    }

    /// <summary>By raft when none is given; names only, in any letter case (never a number).</summary>
    private static HarvestYieldGrouping? ParseGrouping(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "raft" => HarvestYieldGrouping.Raft,
        "zone" => HarvestYieldGrouping.Zone,
        "cycle" => HarvestYieldGrouping.Cycle,
        _ => null,
    };
}
