namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-02 step 2; the client has asked with MSG-GEN-04 and proposed the remaining kg of the lot as the leftover kg.</summary>
/// <param name="LeftoverKg">The kg of the lot that will not be sold: 0 or more, with at most 2 decimals.</param>
/// <param name="Reason"><c>Unsold</c> or <c>Spoiled</c>, required when <paramref name="LeftoverKg"/> is above 0; with 0 it is not kept.</param>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
public sealed record CloseHarvestLotRequest(decimal? LeftoverKg, string? Reason, string? RowVersion);

/// <summary>Answer of a closed lot.</summary>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotClosed(HarvestLotView Lot, string Message, string MessageCode);
