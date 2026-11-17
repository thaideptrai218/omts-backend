using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class EmployeeAppRepository(OmtsDbContext db) : IEmployeeAppRepository
{
    public Task<Employee?> FindEmployedByPhoneAsync(string phoneNumber, DateOnly today, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking()
            .Include(e => e.TypePeriods)
            .FirstOrDefaultAsync(
                e => e.PhoneNumber == phoneNumber
                    && e.StatusId == StatusIds.Employee.Working
                    && (e.LastWorkingDay == null || e.LastWorkingDay >= today),
                cancellationToken);

    public async Task<IReadOnlyList<EmployeeLoginCode>> ListOpenCodesAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.EmployeeLoginCodes
            .Where(c => c.EmployeeId == employeeId && c.ConsumedAt == null && c.CancelledAt == null)
            .ToListAsync(cancellationToken);

    public Task<EmployeeDevice?> GetLinkedDeviceAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.EmployeeDevices.FirstOrDefaultAsync(d => d.EmployeeId == employeeId && d.UnlinkedAt == null, cancellationToken);

    public Task<EmployeeDevice?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.EmployeeDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

    public async Task<IReadOnlyList<EmployeeSession>> ListOpenSessionsAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.EmployeeSessions.Where(s => s.EmployeeId == employeeId && s.RevokedAt == null).ToListAsync(cancellationToken);

    public Task<EmployeeSession?> GetSessionByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        WithEmployeeAndDevice().FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<EmployeeSession?> GetSessionByPreviousRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        WithEmployeeAndDevice().FirstOrDefaultAsync(s => s.PreviousRefreshTokenHash == refreshTokenHash, cancellationToken);

    public Task<EmployeeSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.EmployeeSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public Task<bool> IsSessionActiveAsync(
        Guid employeeId, Guid sessionId, Guid deviceId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.EmployeeSessions.AnyAsync(
            s => s.Id == sessionId
                && s.EmployeeId == employeeId
                && s.EmployeeDeviceId == deviceId
                && s.RevokedAt == null
                && s.AbsoluteExpiresAt > now
                && s.EmployeeDevice.UnlinkedAt == null
                && s.Employee.StatusId == StatusIds.Employee.Working
                && (s.Employee.LastWorkingDay == null || s.Employee.LastWorkingDay >= today),
            cancellationToken);

    public Task<EmployeeDevice?> GetPushTargetAsync(Guid employeeId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.EmployeeDevices.AsNoTracking().FirstOrDefaultAsync(
            d => d.EmployeeId == employeeId
                && d.UnlinkedAt == null
                && db.EmployeeSessions.Any(s => s.EmployeeDeviceId == d.Id && s.RevokedAt == null && s.AbsoluteExpiresAt > now),
            cancellationToken);

    public void Add(EmployeeLoginCode code) => db.EmployeeLoginCodes.Add(code);

    public void Add(EmployeeDevice device) => db.EmployeeDevices.Add(device);

    public void Add(EmployeeSession session) => db.EmployeeSessions.Add(session);

    private IQueryable<EmployeeSession> WithEmployeeAndDevice() =>
        db.EmployeeSessions
            .Include(s => s.Employee).ThenInclude(e => e.TypePeriods)
            .Include(s => s.EmployeeDevice)
            .AsSplitQuery();
}
