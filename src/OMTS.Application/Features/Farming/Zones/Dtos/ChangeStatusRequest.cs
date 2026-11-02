using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

/// <summary>Discontinue / reactivate body; the reason is optional and goes to the audit log.</summary>
public sealed record ChangeStatusRequest(string? Reason, string? RowVersion);
