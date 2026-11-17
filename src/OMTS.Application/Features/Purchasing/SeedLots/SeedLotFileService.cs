using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Purchasing.SeedLots;

/// <summary>The seed lot list as .xlsx: every lot the filters match, in the list's order. Access is that of the purchase receipt export, which the API checks. Text cells are written as text.</summary>
public sealed class SeedLotFileService(SeedLotService lots, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    /// <summary>The most rows one file holds; a wider period is exported in parts.</summary>
    public const int MaxRows = 10_000;

    private static readonly string[] Headers =
    [
        "Seed lot", "Receipt", "Received at", "Supplier", "Seed item", "Product", "Unit", "Received", "Returned", "In nursery", "Nursery loss", "Seeded", "Issued", "Remaining", "State", "Last used at",
    ];

    public async Task<Result<ExportedFile>> ExportAsync(SeedLotListQuery query, CancellationToken cancellationToken)
    {
        var found = await lots.ListAllAsync(query, MaxRows, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        try
        {
            var sheet = new SpreadsheetSheet("Seed lots", Headers, found.Value!.Select(Row).ToList());
            return new ExportedFile($"seed-lots-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([sheet]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static IReadOnlyList<object?> Row(SeedLotRegisterRow lot) =>
    [
        lot.SeedLotCode, lot.ReceiptCode, lot.ReceivedAt.ToOffset(BusinessClock.UtcOffset).DateTime, $"{lot.Supplier.Code} {lot.Supplier.Name}".Trim(), $"{lot.Item.Code} {lot.Item.Name}".Trim(),
        lot.Product is { } product ? $"{product.Code} {product.Name}".Trim() : null, lot.UnitName,
        lot.ReceivedQty, lot.ReturnedQty, lot.NursingQty, lot.NurseryLossQty, lot.SeededQty, lot.IssuedOtherQty, lot.RemainingQty, lot.State.ToString(),
        lot.LastUsedAt?.ToOffset(BusinessClock.UtcOffset).DateTime,
    ];
}
