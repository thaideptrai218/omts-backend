using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The harvest lot list as .xlsx: every lot the filters match in the list's order, with the same zone rule as the list (Harvests View in the lot's zone), for callers who may export harvest lots in one of their zones. Text cells are written as text.
/// </summary>
public sealed class HarvestLotRegisterFileService(HarvestLotRegisterService register, ZoneAccessGuard zoneAccess, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    private const int PageSize = 100;

    /// <summary>The most rows one file holds; a wider period is exported in parts.</summary>
    public const int MaxRows = 10_000;

    private static readonly string[] Headers =
    [
        "Lot code", "Harvested at", "Zone", "Raft", "Products", "Cages", "Estimated kg", "Weighed kg", "Weighed at", "Sold kg", "Remaining kg", "Lost kg", "Harvest plan", "Status",
        "Close reason", "Close date", "Recorded by",
    ];

    public async Task<Result<ExportedFile>> ExportAsync(HarvestLotListQuery query, CancellationToken cancellationToken)
    {
        if ((await zoneAccess.PermittedZonesAsync(PermissionFunctions.HarvestLots, PermissionAction.Export, cancellationToken)).IsEmpty)
        {
            return AppError.Forbidden;
        }

        var rows = new List<IReadOnlyList<object?>>();
        for (var page = 1; rows.Count < MaxRows; page++)
        {
            var found = await register.SearchAsync(query with { Page = page, PageSize = PageSize }, cancellationToken);
            if (!found.IsSuccess)
            {
                return found.Error!;
            }

            var value = found.Value!;
            rows.AddRange(value.Items.Select(Row));
            if (page * PageSize >= value.Total)
            {
                break;
            }
        }

        try
        {
            var sheet = new SpreadsheetSheet("Harvest lots", Headers, rows.Take(MaxRows).ToList());
            return new ExportedFile($"harvest-lots-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([sheet]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static IReadOnlyList<object?> Row(HarvestLotRegisterItem lot) =>
    [
        lot.LotCode, lot.HarvestedAt.ToOffset(BusinessClock.UtcOffset).DateTime, lot.ZoneCode, lot.RaftCode, string.Join(", ", lot.Products.Select(p => $"{p.Code} {p.Name}")),
        lot.CageCount, lot.EstimatedKg, lot.WeighedKg, lot.WeighedAt?.ToOffset(BusinessClock.UtcOffset).DateTime, lot.Figures.SoldKg, lot.Figures.RemainingKg, lot.Figures.LostKg,
        lot.HarvestPlan?.Code, lot.Status, lot.CloseReason?.ToString(), lot.CloseDate?.ToDateTime(TimeOnly.MinValue), lot.RecordedByName,
    ];
}
