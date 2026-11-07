using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-02 close a lot still for sale (Open or Reopened) with the leftover kg and, when it is above 0, the reason Unsold or Spoiled (BR-HARVEST-04): a Zone Manager of the lot's zone, any of
/// them and not only who recorded it, or the Cooperative Owner; never an Accountant (§I.4.4). Today is the close date. A lot of a Closed cycle or a discontinued raft is closed as well, as BR-CYCLE-04
/// locks cultivation records, not sales. The close is audited with its before and after values and sends no notification. A sale or the daily close check in the same moment changes the lot
/// row too, so one of them fails.
/// </summary>
public sealed class HarvestLotClosingService(
    IHarvestLotRepository lots,
    HarvestLotStatusService statuses,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The two reasons a Zone Manager chooses from; the other two belong to the sale tick and the daily check.</summary>
    private static readonly HarvestLotCloseReason[] ChosenReasons = [HarvestLotCloseReason.Unsold, HarvestLotCloseReason.Spoiled];

    public async Task<Result<HarvestLotClosed>> CloseAsync(Guid lotId, CloseHarvestLotRequest request, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        var (refused, leftoverKg, reason) = await ReadAsync(lot, request.LeftoverKg, request.Reason, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (statuses.Close(lot, timeProvider.Today(), leftoverKg, reason) is { } notClosed)
        {
            return notClosed;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Harvest lot");
        return new HarvestLotClosed(await views.ViewAsync(lot, lot.Raft, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>
    /// HARVEST-02.1, the extension point of CLAIM-03: the Zone Manager answered that the lot of discarded oysters is no longer for sale (MSG-HARVEST-02). The same checks as
    /// <see cref="CloseAsync"/> on a lot tracked with its raft (<see cref="IHarvestLotRepository.GetForEditAsync"/>), without the form's row version and without saving: the sales return's
    /// one save holds the close and its audit row. The leftover kg covers only the oysters still unsold, never the discarded kg. Null when the lot is closed.
    /// </summary>
    public async Task<AppError?> CloseWithoutSavingAsync(HarvestLot lot, decimal? leftoverKg, string? reason, CancellationToken cancellationToken)
    {
        var (refused, leftover, chosen) = await ReadAsync(lot, leftoverKg, reason, cancellationToken);
        return refused ?? statuses.Close(lot, timeProvider.Today(), leftover, chosen);
    }

    /// <summary>MSG-GEN-08 outside the caller's zones, MSG-GEN-09 when the lot is no longer for sale, MSG-GEN-01 naming each invalid field (HARVEST-02.0.E1); else the values to close with.</summary>
    private async Task<(AppError? Refused, decimal LeftoverKg, HarvestLotCloseReason? Reason)> ReadAsync(
        HarvestLot lot, decimal? leftoverKg, string? reason, CancellationToken cancellationToken)
    {
        if (!await zoneAccess.AllowsAsync(PermissionFunctions.HarvestLots, PermissionAction.Edit, lot.Raft.ZoneId, cancellationToken))
        {
            return (AppError.Forbidden, 0m, null);
        }

        if (!lot.IsForSale)
        {
            return (statuses.NotForSale(lot), 0m, null);
        }

        var check = new FieldCheck();
        // 3 decimals, as the sold kg (InvoiceMoney.IsKg): the proposed leftover is the remaining kg after the sales, and the column keeps 3.
        check.Check(leftoverKg is >= 0m and <= PurchaseReceiptValidator.MaxQuantity && decimal.Round(leftoverKg.Value, 3) == leftoverKg.Value, HarvestLotStatusService.LeftoverKgField);
        HarvestLotCloseReason? chosen = null;
        if (leftoverKg > 0m)
        {
            chosen = ChosenReasons.Where(r => string.Equals(r.ToString(), reason?.Trim(), StringComparison.OrdinalIgnoreCase)).Cast<HarvestLotCloseReason?>().FirstOrDefault();
            check.Check(chosen is not null, HarvestLotStatusService.CloseReasonField);
        }

        return check.IsValid ? (null, leftoverKg!.Value, chosen) : (AppError.InvalidFields(check.Invalid), 0m, null);
    }
}
