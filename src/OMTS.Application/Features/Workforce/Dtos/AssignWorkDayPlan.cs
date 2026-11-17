namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record AssignWorkDayPlan(IReadOnlyList<Guid> EmployeeIds, string? RowVersion);
