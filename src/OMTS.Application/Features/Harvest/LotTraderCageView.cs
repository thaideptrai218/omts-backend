namespace OMTS.Application.Features.Harvest;

/// <summary>One cage as the trader weighed it, in the order weighed; compared with the cooperative's cage at the same position.</summary>
/// <param name="Number">The number given when the cage was weighed; a removed cage leaves a gap, so compare and show by position in the list.</param>
/// <param name="WeighedAt">When the cage was weighed, or last corrected.</param>
public sealed record LotTraderCageView(Guid Id, int Number, decimal Kg, DateTimeOffset WeighedAt);
