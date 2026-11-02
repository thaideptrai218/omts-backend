using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IWorkforceRepository
{
    Task<IReadOnlyList<JobType>> JobsAsync(CancellationToken ct);
    Task<JobType?> JobAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Employee>> EmployeesAsync(PermittedZones zones, CancellationToken ct);
    Task<Employee?> MyEmployeeAsync(Guid accountId, CancellationToken ct);
    Task<IReadOnlyList<WorkAssignment>> AssignmentsAsync(DateOnly from, DateOnly to, PermittedZones zones, Guid? employeeId, CancellationToken ct);
    Task<WorkAssignment?> AssignmentAsync(Guid id, CancellationToken ct);
    Task<WorkAssignment?> EmployeeAssignmentAsync(Guid employeeId, DateOnly date, CancellationToken ct);
    Task<AttendanceRecord?> AttendanceAsync(Guid employeeId, DateOnly date, CancellationToken ct);
    Task<IReadOnlyList<AttendanceRecord>> AttendanceHistoryAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<AttendanceSettings>> SettingsAsync(CancellationToken ct);
    Task<IReadOnlyList<EmployeeLeaveRequest>> LeaveAsync(Guid? employeeId, CancellationToken ct);
    Task<EmployeeLeaveRequest?> LeaveRequestAsync(Guid id, CancellationToken ct);
    Task<EmployeeLeaveRequest?> LeaveOperationAsync(Guid employeeId, Guid operationId, CancellationToken ct);
    Task<bool> HasApprovedLeaveAsync(Guid employeeId, DateOnly date, CancellationToken ct);
    Task<AttendanceQr?> QrAsync(string hash, CancellationToken ct);
    Task<AttendanceQr?> EmployeeQrAsync(Guid employeeId, CancellationToken ct);
    Task<bool> HasLockedPayrollFromAsync(DateOnly from, CancellationToken ct);
    Task<bool> HasLockedPayrollPeriodAsync(DateOnly from, DateOnly to, CancellationToken ct);
    void AddAssignment(WorkAssignment assignment);
    void AddAttendance(AttendanceRecord attendance);
    void AddPeriod(AttendanceJobPeriod period);
    void AddJob(JobType job);
    void AddLeave(EmployeeLeaveRequest request);
    void AddSettings(AttendanceSettings settings);
    void AddQr(AttendanceQr qr);
}
