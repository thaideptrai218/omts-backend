namespace OMTS.Application.Features.Workforce;

public sealed record ChangeCrewRequest(Guid RaftId, Guid JobTypeId, IReadOnlyList<Guid> EmployeeIds);
