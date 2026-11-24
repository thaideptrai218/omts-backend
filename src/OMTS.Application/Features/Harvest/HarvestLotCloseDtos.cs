namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-02 step 2; the client has asked with MSG-GEN-04 and proposed the remaining kg of the lot as the leftover kg.</summary>
/// <param name="LeftoverKg">The kg of the lot that will not be sold: 0 or more, with at most 2 decimals.</param>
/// <param name="Reason"><c>Unsold</c> or <c>Spoiled</c>, required when <paramref name="LeftoverKg"/> is above 0; with 0 it is not kept.</param>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
/// <param name="OwnerApproved">Only the Cooperative Owner: close although an employee of the lot with work that day is not checked out (MSG-LABOR-37); needs <paramref name="ApprovalReason"/> (1 to 1000 characters; apart from <paramref name="Reason"/>, the reason of the leftover).</param>
public sealed record CloseHarvestLotRequest(decimal? LeftoverKg, string? Reason, string? RowVersion, bool OwnerApproved = false, string? ApprovalReason = null);

/// <summary>Answer of a closed lot.</summary>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotClosed(HarvestLotView Lot, string Message, string MessageCode);

/// <summary>Reopening a lot closed Unsold, Spoiled or by the time limit.</summary>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
/// <param name="Reason">Why the lot is reopened, 1 to 1000 characters.</param>
public sealed record ReopenHarvestLotRequest(string? RowVersion, string? Reason);

/// <summary>Answer of a lot weighed or reopened.</summary>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotChanged(HarvestLotView Lot, string Message, string MessageCode);
