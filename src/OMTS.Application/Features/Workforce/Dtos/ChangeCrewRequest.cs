namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record ChangeCrewRequest(Guid RaftId, Guid JobTypeId, IReadOnlyList<Guid> EmployeeIds);
