namespace OMTS.Application.Features.Workforce;

public sealed record WorkforceLookups(IReadOnlyList<JobOption> Jobs, IReadOnlyList<EmployeeOption> Employees);
