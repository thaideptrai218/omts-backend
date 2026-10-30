namespace OMTS.Application.Features.Workforce;

public sealed record MyWorkforceView(Guid? EmployeeId, string? Name, IReadOnlyList<WorkAssignmentView> Assignments, IReadOnlyList<AttendanceDayView> Attendance, IReadOnlyList<LeaveRequestView> Requests);
