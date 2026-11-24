namespace OMTS.Application.Features.Harvest;

/// <summary>The trader agrees to the weighing the cooperative completed (owner decision of 24/11/2026).</summary>
/// <param name="RowVersion">The version of the lot the weighing sheet was read at (NFR-09): a weighing completed again since answers MSG06.</param>
public sealed record AgreeLotWeighingRequest(string? RowVersion);
