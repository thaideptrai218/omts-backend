using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// Weighs a lot: the actual kg of each product line (or of the lot, when it has no lines) replace the estimate for every figure, and the lot becomes for sale (Awaiting weighing to Open). A Zone Manager of the
/// lot's zone, any of them and not only who recorded it, or the Cooperative Owner; never an Accountant (§I.4.4). A lot that is Open or Reopened is weighed again the same way: the Harvest rows of the product
/// ledger are undone and written at the new kg, and nothing else on the ledger changes. The weighing is audited with its before and after values; the days before automatic close count from the day of the
/// latest weighing of a lot that was never reopened. The crew is not checked out by a weighing.
/// </summary>
public sealed class HarvestLotWeighingService(
    IHarvestLotRepository lots,
    HarvestLotProductLineService productLines,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string WeighedAtField = "Weighed at";
    public const string WeighedKgField = "Weighed kg";
    public const string LinesField = "Lines";

    public async Task<Result<HarvestLotChanged>> WeighAsync(Guid lotId, WeighHarvestLotRequest request, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.HarvestLots, PermissionAction.Edit, lot.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (lot.StatusId is not (StatusIds.HarvestLot.AwaitingWeighing or StatusIds.HarvestLot.Open or StatusIds.HarvestLot.Reopened))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
        }

        var now = timeProvider.GetUtcNow();
        var read = Read(lot, request, now);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (at, kgByLine, totalKg) = read.Value!;
        var again = lot.WeighedKg is not null;
        var before = HarvestLotAudit.WeighingFacts(lot, statusCatalog.CodeOf(lot.StatusId));
        if (lot.ProductLines.Count > 0)
        {
            await productLines.WeighAsync(lot, lot.Raft.Code, kgByLine, cancellationToken);
        }

        lot.WeighedKg = totalKg;
        lot.WeighedAt = at;
        lot.WeighedOn = BusinessClock.ToBusinessDate(at);
        lot.WeighedByAccountId = currentUser.AccountId;
        lot.StatusId = lot.StatusId == StatusIds.HarvestLot.AwaitingWeighing ? StatusIds.HarvestLot.Open : lot.StatusId;
        audit.Record(HarvestLotAudit.Entry(again ? HarvestLotAudit.Reweighed : HarvestLotAudit.Weighed, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = HarvestLotAudit.WeighingFacts(lot, statusCatalog.CodeOf(lot.StatusId)),
        });
        unitOfWork.MarkChanged(lot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Harvest lot");
        return new HarvestLotChanged(await views.ViewAsync(lot, lot.Raft, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>MSG-GEN-01 naming each invalid field: the time of the weighing, and the weighed kg of every line (or of the lot); else the values to weigh with.</summary>
    private static Result<(DateTimeOffset At, IReadOnlyDictionary<int, decimal> KgByLine, decimal TotalKg)> Read(HarvestLot lot, WeighHarvestLotRequest request, DateTimeOffset now)
    {
        var check = new FieldCheck();
        var at = request.WeighedAt?.ToOffset(BusinessClock.UtcOffset) ?? now.ToOffset(BusinessClock.UtcOffset);
        check.Check(at >= lot.HarvestedAt && at <= now + PurchaseReceiptValidator.ClockTolerance, WeighedAtField);

        var kgByLine = new Dictionary<int, decimal>();
        decimal total;
        if (lot.ProductLines.Count == 0)
        {
            check.Check(InvoiceMoney.IsKg(request.WeighedKg), WeighedKgField);
            total = request.WeighedKg ?? 0m;
        }
        else
        {
            var sent = request.Lines ?? [];
            // Every line of the lot exactly once; a number that is not a line of the lot, or twice, is a mistake of the form.
            check.Check(sent.All(l => l is not null) && sent.Count == lot.ProductLines.Count && sent.Select(l => l?.LineNumber).Distinct().Count() == sent.Count, LinesField);
            foreach (var line in lot.ProductLines.OrderBy(l => l.LineNumber))
            {
                var weighed = sent.FirstOrDefault(l => l?.LineNumber == line.LineNumber);
                var field = $"Line {line.LineNumber} weighed kg";
                if (weighed is null)
                {
                    check.Check(false, field);
                    continue;
                }

                check.Check(InvoiceMoney.IsKg(weighed.WeighedKg), field);
                kgByLine[line.LineNumber] = weighed.WeighedKg ?? 0m;
            }

            total = kgByLine.Values.Sum();
        }

        // The column holds up to 18 digits: a sum above it would fail the save instead of being told.
        check.Check(total <= PurchaseReceiptValidator.MaxQuantity, WeighedKgField);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        return (at, kgByLine, total);
    }
}
