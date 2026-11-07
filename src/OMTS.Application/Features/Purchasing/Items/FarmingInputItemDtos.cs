using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

public sealed record SaveFarmingInputItemRequest(
    string? Code, string? Name, InputItemGroup? Group, string? Unit, string? Description, string? RowVersion = null);

/// <param name="IsUsed">On a purchase receipt (or planned by a cultivation cycle): group and unit can no longer change.</param>
public sealed record FarmingInputItemView(
    Guid Id, string Code, string Name, InputItemGroup Group, string Unit, string? Description, bool IsUsed, int StatusId, string Status, string RowVersion);

/// <summary>What the Add item form starts with (PURCHASE-08 step 3).</summary>
/// <param name="Code">The proposed code (GBR-03), which the Owner may replace.</param>
/// <param name="SeedUnit">The unit every Seed item uses, filled in for a Seed item; null before the first Seed item.</param>
public sealed record NewFarmingInputItemProposal(string Code, string? SeedUnit);
