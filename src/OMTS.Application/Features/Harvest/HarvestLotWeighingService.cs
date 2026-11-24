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
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// Completes the weighing of a lot from the cages weighed at the port (owner decision of 24/11/2026; <see cref="HarvestLotCageService"/> saves them one by one): the kg of each product line is the sum of
/// its cages (or the kg of the lot the sum of every cage, when it has no lines), they replace the estimate for every figure, and the lot becomes for sale (Awaiting weighing to Open). A Zone Manager of the
/// lot's zone, any of them and not only who recorded it, or the Cooperative Owner; never an Accountant (§I.4.4). A lot that is Open or Reopened is completed again the same way after a cage was corrected:
/// the Harvest rows of the product ledger are undone and written at the new kg, and nothing else on the ledger changes. The weighing is audited with its before and after values; the days before automatic
/// close count from the day of the latest weighing of a lot that was never reopened. The crew is not checked out by a weighing.
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
    public const string CagesField = "Cages";

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

    /// <summary>
    /// MSG-GEN-01 naming each invalid field: the time of the weighing, no cage weighed (<c>Cages</c>), a cage whose product is not a line of the lot (<c>Cage {number} product</c>, after a correction of
    /// the lines) and a line with no cage (<c>Line {n} cages</c>); else the kg of each line, summed from its cages, and of the lot.
    /// </summary>
    private static Result<(DateTimeOffset At, IReadOnlyDictionary<int, decimal> KgByLine, decimal TotalKg)> Read(HarvestLot lot, WeighHarvestLotRequest request, DateTimeOffset now)
    {
        var check = new FieldCheck();
        var at = request.WeighedAt?.ToOffset(BusinessClock.UtcOffset) ?? now.ToOffset(BusinessClock.UtcOffset);
        check.Check(at >= lot.HarvestedAt && at <= now + PurchaseReceiptValidator.ClockTolerance, WeighedAtField);

        var cages = lot.Cages.OrderBy(c => c.Number).ToList();
        check.Check(cages.Count > 0, CagesField);
        var kgByLine = new Dictionary<int, decimal>();
        decimal total;
        if (lot.ProductLines.Count == 0)
        {
            // A lot with no lines weighs as one: every cage counts, and none can name a product.
            foreach (var cage in cages.Where(c => c.ProductId is not null))
            {
                check.Check(false, HarvestLotCageService.CageProductField(cage.Number));
            }

            total = cages.Sum(c => c.Kg);
        }
        else
        {
            var products = lot.ProductLines.Select(l => l.ProductId).ToHashSet();
            foreach (var cage in cages.Where(c => c.ProductId is not { } productId || !products.Contains(productId)))
            {
                check.Check(false, HarvestLotCageService.CageProductField(cage.Number));
            }

            // Every line needs at least one cage: its weighed kg is above 0, as the ledger writes one Harvest row per line.
            foreach (var line in lot.ProductLines.OrderBy(l => l.LineNumber))
            {
                var kg = cages.Where(c => c.ProductId == line.ProductId).Sum(c => c.Kg);
                check.Check(kg > 0m, $"Line {line.LineNumber} cages");
                kgByLine[line.LineNumber] = kg;
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
