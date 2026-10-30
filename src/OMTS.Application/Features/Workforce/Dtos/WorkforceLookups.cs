namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record WorkforceLookups(IReadOnlyList<JobOption> Jobs, IReadOnlyList<EmployeeOption> Employees);
