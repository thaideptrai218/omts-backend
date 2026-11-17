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

/// <summary>The weighed kg of one product line of a lot.</summary>
/// <param name="LineNumber">The number of the line on the lot, from 1.</param>
/// <param name="WeighedKg">Above 0, at most 3 decimals.</param>
public sealed record WeighedLineInput(int LineNumber, decimal? WeighedKg);

/// <summary>Weighing a lot, or weighing it again: the weighed kg of every product line, or of the lot when it has no lines.</summary>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
/// <param name="WeighedAt">When the oysters were weighed; left out it is now. Not before the harvest and not in the future.</param>
/// <param name="Lines">One entry for each product line of the lot, none missing and none twice; ignored for a lot with no lines.</param>
/// <param name="WeighedKg">The weighed kg of a lot with no product lines; ignored when the lot has lines (the kg of the lot are then the sum of the lines).</param>
public sealed record WeighHarvestLotRequest(string? RowVersion, DateTimeOffset? WeighedAt, IReadOnlyList<WeighedLineInput>? Lines, decimal? WeighedKg);

/// <summary>Reopening a lot closed Unsold, Spoiled or by the time limit.</summary>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
/// <param name="Reason">Why the lot is reopened, 1 to 1000 characters.</param>
public sealed record ReopenHarvestLotRequest(string? RowVersion, string? Reason);

/// <summary>Answer of a lot weighed or reopened.</summary>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotChanged(HarvestLotView Lot, string Message, string MessageCode);
