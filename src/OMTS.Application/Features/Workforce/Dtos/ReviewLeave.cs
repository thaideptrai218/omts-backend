namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record ReviewLeave(bool Approved, string Response, string? RowVersion);
