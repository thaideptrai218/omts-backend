namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record AssignWorkRequest(Guid RaftId, Guid JobTypeId, DateOnly Date, IReadOnlyList<Guid> EmployeeIds, bool MoveExisting = false);
