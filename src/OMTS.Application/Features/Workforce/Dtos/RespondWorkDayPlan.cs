namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record RespondWorkDayPlan(bool Approved, string? Reason, string? RowVersion);
