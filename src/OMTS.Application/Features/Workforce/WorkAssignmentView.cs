namespace OMTS.Application.Features.Workforce;

public sealed record WorkAssignmentView(Guid Id, Guid EmployeeId, string EmployeeName, Guid ZoneId, Guid? RaftId, string? RaftCode, Guid? JobTypeId, string Job, DateOnly Date, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt);
