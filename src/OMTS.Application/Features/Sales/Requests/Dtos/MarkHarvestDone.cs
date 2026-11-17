namespace OMTS.Application.Features.Sales.Requests.Dtos;

/// <summary>Marks the harvest of a request done, or clears the mark (decision of 11/11/2026). <paramref name="RowVersion"/> is the version of the request.</summary>
public sealed record MarkHarvestDone(string? RowVersion);
