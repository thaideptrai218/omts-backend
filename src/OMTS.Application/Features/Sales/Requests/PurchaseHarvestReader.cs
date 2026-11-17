using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Requests;

/// <summary>The harvest of a request as the staff detail shows it (decision of 11/11/2026): its lots on every raft, the mark of the Cooperative Owner and what may be done with it.</summary>
public sealed class PurchaseHarvestReader(IHarvestLotRepository lots, IPortWeighingRepository weighings, IAccountRepository accounts)
{
    public async Task<PurchaseHarvestView> ReadAsync(PurchaseRequest request, bool isOwner, CancellationToken cancellationToken)
    {
        var found = await lots.ListOfPurchaseRequestAsync(request.Id, cancellationToken);
        var people = found.Select(l => l.RecordedByAccountId).ToHashSet();
        if (request.HarvestCompletedByAccountId is { } markedBy)
        {
            people.Add(markedBy);
        }

        var names = await accounts.GetDisplayNamesAsync(people, cancellationToken);
        var weighed = request.HarvestCompletedAt is not null && await weighings.AnyOfRequestAsync(request.Id, cancellationToken);
        return new PurchaseHarvestView(
            found.Sum(l => l.CageCount),
            found.Select(l => new PurchaseHarvestLotView(
                l.Id, l.LotCode, l.Raft.Zone.Code, l.Raft.Code, l.CageCount, l.EstimatedKg, l.HarvestedAt, names.GetValueOrDefault(l.RecordedByAccountId))).ToList(),
            request.HarvestCompletedAt,
            request.HarvestCompletedByAccountId is { } by ? names.GetValueOrDefault(by) : null,
            isOwner && request.StatusId == StatusIds.PurchaseRequest.Accepted && request.HarvestCompletedAt is null && found.Count > 0,
            isOwner && request.HarvestCompletedAt is not null && !weighed);
    }
}
