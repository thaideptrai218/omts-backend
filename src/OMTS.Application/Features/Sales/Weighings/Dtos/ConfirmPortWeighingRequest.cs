namespace OMTS.Application.Features.Sales.Weighings.Dtos;

/// <param name="RowVersion">The version the trader read: figures corrected since then are not confirmed unseen.</param>
public sealed record ConfirmPortWeighingRequest(string? RowVersion);
