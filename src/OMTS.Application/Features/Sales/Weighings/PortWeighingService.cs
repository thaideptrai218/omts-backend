using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.Weighings.Dtos;
using OMTS.Application.Features.Sales.Weighings.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Weighings;

/// <summary>
/// The weighing at the port on the staff side (decision of 09/11/2026): the kg of each cage of an accepted request, from lots already harvested and for sale, corrected until the trader
/// confirms. Those who sell at the port record it (SALES_INVOICES Create), those who view invoices read it. The API checks the role, this every zone.
/// Decision of 11/11/2026: a request is weighed only once the Cooperative Owner marked its harvest done, from its own lots, and no lot past the cages it holds.
/// </summary>
public sealed class PortWeighingService(
    IPortWeighingRepository weighings,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    IHarvestLotRepository lots,
    HarvestLotStatusService lotStatuses,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard versions,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string ObjectName = "Port weighing";
    public const string UnconfirmedStatus = "not confirmed yet";
    public const string ConfirmedStatus = "confirmed";
    public const string InvoicedStatus = "invoiced";

    public async Task<Result<IReadOnlyList<PortWeighingView>>> ListOfRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.View, cancellationToken);
        if (permitted.IsEmpty)
        {
            return AppError.Forbidden;
        }

        IReadOnlyList<PortWeighingView> rows = (await weighings.ListOfRequestAsync(requestId, cancellationToken))
            .Where(w => InZones(w, permitted))
            .Select(PortWeighingViews.Of)
            .ToList();
        return Result<IReadOnlyList<PortWeighingView>>.Success(rows);
    }

    public async Task<Result<PortWeighingView>> GetAsync(Guid weighingId, CancellationToken cancellationToken)
    {
        var weighing = await weighings.GetAsync(weighingId, cancellationToken);
        if (weighing is null)
        {
            return AppError.NotFound;
        }

        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.View, cancellationToken);
        return InZones(weighing, permitted) ? PortWeighingViews.Of(weighing) : AppError.Forbidden;
    }

    public async Task<Result<PortWeighingView>> CreateAsync(CreatePortWeighingRequest input, CancellationToken cancellationToken)
    {
        var (entries, invalid) = PortWeighingEntryReader.Read(input.Lots);
        if (input.PurchaseRequestId is null)
        {
            invalid.Insert(0, "Purchase request");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken);
        var request = await requests.GetForEditAsync(input.PurchaseRequestId!.Value, cancellationToken);
        if (request is null)
        {
            return AppError.Invalid("Purchase request");
        }

        if (await RefusedRequestAsync(request, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await weighings.HasUnconfirmedAsync(request.Id, cancellationToken))
        {
            return AppError.Conflict(UserMessages.WrongStatus(ObjectName, UnconfirmedStatus));
        }

        if (await RefusedLotsAsync(entries, permitted, request.Id, null, cancellationToken) is { } badLots)
        {
            return badLots;
        }

        var weighing = new PortWeighing
        {
            PurchaseRequestId = request.Id,
            PurchaseRequest = request,
            WeighedAt = timeProvider.GetUtcNow(),
            WeighedByAccountId = currentUser.AccountId!.Value,
        };
        var number = 0;
        foreach (var (lot, kg) in Cages(entries))
        {
            weighing.Cages.Add(new PortWeighingCage { CageNumber = ++number, HarvestLotId = lot, Kg = kg });
        }

        weighings.Add(weighing);
        // The Owner clears the harvest mark only while the request has no weighing: a parallel clear read the row before this weighing, so one of the two fails with MSG06.
        unitOfWork.MarkChanged(request);
        audit.Record(new AuditEntry("PortWeighing.Recorded", nameof(PortWeighing)) { RecordId = weighing.Id, RecordCode = request.Code, After = new { Lots = entries } });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(weighing.Id, cancellationToken);
    }

    /// <summary>A correction before the trader confirms: the new figures replace the old ones, which stay in the audit log.</summary>
    public async Task<Result<PortWeighingView>> UpdateAsync(Guid weighingId, UpdatePortWeighingRequest input, CancellationToken cancellationToken)
    {
        var (entries, invalid) = PortWeighingEntryReader.Read(input.Lots);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var weighing = await weighings.GetForEditAsync(weighingId, cancellationToken);
        if (weighing is null)
        {
            return AppError.NotFound;
        }

        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken);
        if (!InZones(weighing, permitted))
        {
            return AppError.Forbidden;
        }

        if (weighing.IsConfirmed)
        {
            return AppError.Conflict(UserMessages.WrongStatus(ObjectName, ConfirmedStatus));
        }

        if (await versions.CheckAsync(weighing, input.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (await RefusedLotsAsync(entries, permitted, weighing.PurchaseRequestId, weighing.Id, cancellationToken) is { } badLots)
        {
            return badLots;
        }

        var before = weighing.Cages.OrderBy(c => c.CageNumber).Select(c => new { c.CageNumber, c.HarvestLotId, c.Kg }).ToList();
        Replace(weighing, entries);
        unitOfWork.MarkChanged(weighing);
        audit.Record(new AuditEntry("PortWeighing.Corrected", nameof(PortWeighing))
        {
            RecordId = weighing.Id,
            RecordCode = weighing.PurchaseRequest.Code,
            Before = new { Cages = before },
            After = new { Lots = entries },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(weighing.Id, cancellationToken);
    }

    /// <summary>Overwrites the cages in place, so a number is never held by two rows while saving.</summary>
    private void Replace(PortWeighing weighing, IReadOnlyList<PortWeighingLotEntries> entries)
    {
        var kept = weighing.Cages.OrderBy(c => c.CageNumber).ToList();
        var number = 0;
        foreach (var (lot, kg) in Cages(entries))
        {
            number++;
            if (number <= kept.Count)
            {
                kept[number - 1].HarvestLotId = lot;
                kept[number - 1].Kg = kg;
            }
            else
            {
                weighings.AddCage(new PortWeighingCage { PortWeighingId = weighing.Id, CageNumber = number, HarvestLotId = lot, Kg = kg });
            }
        }

        weighings.RemoveCages(kept.Skip(number).ToList());
    }

    private static IEnumerable<(Guid Lot, decimal Kg)> Cages(IReadOnlyList<PortWeighingLotEntries> entries) =>
        entries.SelectMany(e => e.CageKgs.Select(kg => (e.LotId, kg)));

    private async Task<AppError?> RefusedRequestAsync(PurchaseRequest request, CancellationToken cancellationToken)
    {
        if (request.StatusId != StatusIds.PurchaseRequest.Accepted)
        {
            return AppError.Conflict(UserMessages.WrongStatus(PurchaseRequestRules.ObjectName, statusCatalog.DisplayNameOf(request.StatusId)));
        }

        if (request.HarvestCompletedAt is null)
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.HarvestNotDoneStatus);
        }

        return (await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id)
            ? AppError.Conflict(UserMessages.WrongStatus(PurchaseRequestRules.ObjectName, PurchaseRequestRules.FulfilledStatus))
            : null;
    }

    /// <summary>
    /// Each lot was harvested for the request and holds the cages weighed from it, those of its other weighings included (decision of 11/11/2026), is in a zone of the caller and is
    /// for sale. <paramref name="exceptWeighingId"/> is the weighing being corrected, whose old cages are replaced.
    /// </summary>
    private async Task<AppError?> RefusedLotsAsync(
        IReadOnlyList<PortWeighingLotEntries> entries, PermittedZones permitted, Guid requestId, Guid? exceptWeighingId, CancellationToken cancellationToken)
    {
        var found = await lots.ListForInvoiceAsync(entries.Select(e => e.LotId).ToList(), cancellationToken);
        var weighed = await weighings.CagesByLotOfRequestAsync(requestId, exceptWeighingId, cancellationToken);
        for (var i = 0; i < entries.Count; i++)
        {
            var lot = found.FirstOrDefault(l => l.Id == entries[i].LotId);
            if (lot is null || lot.PurchaseRequestId != requestId)
            {
                return AppError.Invalid($"Lot {i + 1} harvest lot");
            }

            var left = lot.CageCount - weighed.GetValueOrDefault(lot.Id);
            if (entries[i].CageKgs.Count > left)
            {
                return AppError.Invalid($"Lot {i + 1} cage kg", $"Lot {lot.LotCode} has {Math.Max(left, 0)} of its {lot.CageCount} cages left to weigh.");
            }
        }

        return found.Any(l => !permitted.Allows(l.Raft.ZoneId)) ? AppError.Forbidden : lotStatuses.EnsureForSale(found);
    }

    private static bool InZones(PortWeighing weighing, PermittedZones permitted) => weighing.Cages.Any(c => permitted.Allows(c.HarvestLot.Raft.ZoneId));

    private async Task<PortWeighingView> ViewAsync(Guid weighingId, CancellationToken cancellationToken) =>
        PortWeighingViews.Of(await weighings.GetAsync(weighingId, cancellationToken) ?? throw new InvalidOperationException($"Port weighing {weighingId} was just saved and cannot be read."));
}
