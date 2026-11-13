namespace OMTS.Application.Features.Sales.Requests.Dtos;

/// <summary>The harvest of a request for the staff (decision of 11/11/2026): the lots of every raft and whether the Cooperative Owner marked it done.</summary>
/// <param name="HarvestedCages">The cages of all its lots, to set beside the accepted cages: fewer or more is allowed.</param>
/// <param name="Lots">The earliest harvest first.</param>
/// <param name="CanMarkDone">Show "Đã thu xong": the caller is the Owner, the request is Accepted, not marked yet and has a lot.</param>
/// <param name="CanUnmarkDone">The Owner may clear the mark: it is set and the request has no weighing yet.</param>
public sealed record PurchaseHarvestView(
    int HarvestedCages,
    IReadOnlyList<PurchaseHarvestLotView> Lots,
    DateTimeOffset? CompletedAt,
    string? CompletedByName,
    bool CanMarkDone,
    bool CanUnmarkDone);
