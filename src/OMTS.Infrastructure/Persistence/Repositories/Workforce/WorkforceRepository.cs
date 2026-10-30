using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories.Workforce;

internal sealed class WorkforceRepository(OmtsDbContext db) : IWorkforceRepository
{
    public async Task<IReadOnlyList<JobType>> JobsAsync(CancellationToken ct) =>
        await db.Set<JobType>().Where(j => j.StatusId == StatusIds.Usage.InUse).OrderBy(j => j.Name).ToListAsync(ct);
    public Task<JobType?> JobAsync(Guid id, CancellationToken ct) => db.Set<JobType>().FirstOrDefaultAsync(j => j.Id == id, ct);
    public async Task<IReadOnlyList<Employee>> EmployeesAsync(PermittedZones zones, CancellationToken ct) =>
        await db.Employees.Include(e => e.WorkingZones).Where(e => e.StatusId == StatusIds.Employee.Working &&
            (zones.AllZones || e.WorkingZones.Any(w => zones.ZoneIds.Contains(w.ZoneId))))
            .OrderBy(e => e.FullName).ToListAsync(ct);
    public Task<Employee?> MyEmployeeAsync(Guid accountId, CancellationToken ct) =>
        db.Employees.FirstOrDefaultAsync(e => e.AccountId == accountId && e.StatusId == StatusIds.Employee.Working, ct);
    public async Task<IReadOnlyList<WorkAssignment>> AssignmentsAsync(DateOnly from, DateOnly to, PermittedZones zones, Guid? employeeId, CancellationToken ct) =>
        await db.WorkAssignments.Include(a => a.Employee).Include(a => a.Raft).Include(a => a.JobType)
            .Where(a => a.CancelledAt == null && a.WorkDate >= from && a.WorkDate <= to &&
                (employeeId == null || a.EmployeeId == employeeId) && (zones.AllZones || zones.ZoneIds.Contains(a.ZoneId)))
            .OrderBy(a => a.WorkDate).ThenBy(a => a.Employee!.FullName).ToListAsync(ct);
    public Task<WorkAssignment?> AssignmentAsync(Guid id, CancellationToken ct) =>
        db.WorkAssignments.Include(a => a.Employee).Include(a => a.Raft).Include(a => a.JobType).FirstOrDefaultAsync(a => a.Id == id, ct);
    public Task<WorkAssignment?> EmployeeAssignmentAsync(Guid employeeId, DateOnly date, CancellationToken ct) =>
        db.WorkAssignments.Include(a => a.Employee).Include(a => a.Raft).Include(a => a.JobType)
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.WorkDate == date && a.CancelledAt == null, ct);
    public Task<AttendanceRecord?> AttendanceAsync(Guid employeeId, DateOnly date, CancellationToken ct) =>
        db.AttendanceRecords.Include(a => a.JobPeriods).ThenInclude(p => p.JobType)
            .Include(a => a.JobPeriods).ThenInclude(p => p.Raft)
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.WorkDate == date, ct);
    public async Task<IReadOnlyList<AttendanceRecord>> AttendanceHistoryAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.AttendanceRecords.Include(a => a.JobPeriods).ThenInclude(p => p.JobType)
            .Include(a => a.JobPeriods).ThenInclude(p => p.Raft)
            .Where(a => a.EmployeeId == employeeId && a.WorkDate >= from && a.WorkDate <= to)
            .OrderByDescending(a => a.WorkDate).ToListAsync(ct);
    public async Task<IReadOnlyList<AttendanceSettings>> SettingsAsync(CancellationToken ct) =>
        await db.Set<AttendanceSettings>().OrderByDescending(s => s.EffectiveFrom).ToListAsync(ct);
    public async Task<IReadOnlyList<EmployeeLeaveRequest>> LeaveAsync(Guid? employeeId, CancellationToken ct) =>
        await db.Set<EmployeeLeaveRequest>().Include(r => r.Employee).Where(r => employeeId == null || r.EmployeeId == employeeId)
            .OrderBy(r => r.ReviewedAt != null || r.CancelledAt != null).ThenBy(r => r.CreatedAt).ToListAsync(ct);
    public Task<EmployeeLeaveRequest?> LeaveRequestAsync(Guid id, CancellationToken ct) =>
        db.Set<EmployeeLeaveRequest>().Include(r => r.Employee).FirstOrDefaultAsync(r => r.Id == id, ct);
    public Task<EmployeeLeaveRequest?> LeaveOperationAsync(Guid employeeId, Guid operationId, CancellationToken ct) =>
        db.Set<EmployeeLeaveRequest>().Include(r => r.Employee).FirstOrDefaultAsync(r => r.EmployeeId == employeeId && r.ClientOperationId == operationId, ct);
    public Task<bool> HasApprovedLeaveAsync(Guid employeeId, DateOnly date, CancellationToken ct) =>
        db.Set<EmployeeLeaveRequest>().AnyAsync(r => r.EmployeeId == employeeId && r.Approved == true && r.From <= date && r.To >= date, ct);
    public Task<AttendanceQr?> QrAsync(string hash, CancellationToken ct) =>
        db.Set<AttendanceQr>().FirstOrDefaultAsync(q => q.TokenHash == hash, ct);
    public void AddAssignment(WorkAssignment assignment) => db.WorkAssignments.Add(assignment);
    public Task<AttendanceQr?> EmployeeQrAsync(Guid employeeId, CancellationToken ct) =>
        db.Set<AttendanceQr>().FirstOrDefaultAsync(q => q.EmployeeId == employeeId, ct);
    public Task<bool> HasLockedPayrollFromAsync(DateOnly from, CancellationToken ct) =>
        db.LaborSettlements.AnyAsync(p => p.PeriodEnd >= from && p.StatusId != StatusIds.LaborSettlement.Draft, ct);
    public Task<bool> HasLockedPayrollPeriodAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        db.LaborSettlements.AnyAsync(p => p.PeriodStart <= to && p.PeriodEnd >= from && p.StatusId != StatusIds.LaborSettlement.Draft, ct);
    public void AddAttendance(AttendanceRecord attendance) => db.AttendanceRecords.Add(attendance);
    public void AddPeriod(AttendanceJobPeriod period) => db.Set<AttendanceJobPeriod>().Add(period);
    public void AddJob(JobType job) => db.Set<JobType>().Add(job);
    public void AddLeave(EmployeeLeaveRequest request) => db.Set<EmployeeLeaveRequest>().Add(request);
    public void AddSettings(AttendanceSettings settings) => db.Set<AttendanceSettings>().Add(settings);
    public void AddQr(AttendanceQr qr) => db.Set<AttendanceQr>().Add(qr);
}
