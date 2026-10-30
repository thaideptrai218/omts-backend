namespace OMTS.Application.Features.Workforce;

public sealed record ReviewLeave(bool Approved, string Response, string? RowVersion);
