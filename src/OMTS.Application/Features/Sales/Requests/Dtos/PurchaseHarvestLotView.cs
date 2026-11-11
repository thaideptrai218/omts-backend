namespace OMTS.Application.Features.Sales.Requests.Dtos;

/// <summary>A harvest lot recorded for the request (decision of 11/11/2026): which raft, how many cages, when and by whom.</summary>
public sealed record PurchaseHarvestLotView(
    Guid Id, string LotCode, string ZoneCode, string RaftCode, decimal CageCount, decimal EstimatedKg, DateTimeOffset HarvestedAt, string? RecordedByName);
