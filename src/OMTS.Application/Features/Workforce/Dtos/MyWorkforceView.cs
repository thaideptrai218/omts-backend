namespace OMTS.Application.Features.Workforce.Dtos;

public sealed record MyWorkforceView(Guid? EmployeeId, string? Name, IReadOnlyList<WorkAssignmentView> Assignments, IReadOnlyList<AttendanceDayView> Attendance, IReadOnlyList<LeaveRequestView> Requests);
