using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// Reopens a Closed lot by hand, with a reason: a Zone Manager of the lot's zone, any of them, or the Cooperative Owner; never an Accountant (§I.4.4). Only a lot closed Unsold, Spoiled, by the time limit or with
/// nothing left comes back this way; a lot closed by an invoice (sold whole lot) reopens only through that invoice (BR-HARVEST-04). Today is the day the days before automatic close count from; the LotClose rows of
/// the lot on the product ledger are undone, so the kg taken off at the close are back. A sale or the daily close check in the same moment changes the lot row too, so one of them fails.
/// </summary>
public sealed class HarvestLotReopeningService(
    IHarvestLotRepository lots,
    HarvestLotStatusService statuses,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<HarvestLotChanged>> ReopenAsync(Guid lotId, ReopenHarvestLotRequest request, CancellationToken cancellationToken)
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

        if (!HarvestLotStatusService.MayReopenByHand(lot))
        {
            // A lot that is not Closed, or was closed by an invoice: MSG-GEN-09 with the state it is in.
            var status = lot.StatusId == StatusIds.HarvestLot.Closed ? "closed by an invoice" : statusCatalog.DisplayNameOf(lot.StatusId);
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", status));
        }

        var check = new FieldCheck();
        var reason = check.Required(request.Reason, HarvestLotFieldReader.ReasonField, HarvestLotFieldReader.MaxReasonLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (!await statuses.ReopenAsync(lot, timeProvider.Today(), HarvestLotReopenCause.ReopenedByHand, reason: reason, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException($"Harvest lot {lot.LotCode} was checked for a reopen by hand and could not be reopened.");
        }

        unitOfWork.MarkChanged(lot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Harvest lot");
        return new HarvestLotChanged(await views.ViewAsync(lot, lot.Raft, cancellationToken), saved.Text, saved.Code);
    }
}
