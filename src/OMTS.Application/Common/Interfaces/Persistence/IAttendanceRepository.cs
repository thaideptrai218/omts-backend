using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Attendance records (LAB-04): one per person per day across all zones.</summary>
public interface IAttendanceRepository
{
    /// <summary>Records of the zone on the date with person, team and zone, tracked.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListForZoneAsync(Guid zoneId, DateOnly date, CancellationToken cancellationToken);

    /// <summary>Records of the people on the date in any zone, with zone, tracked.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListForPeopleAsync(
        DateOnly date, IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken);

    /// <summary>The record with zone, tracked.</summary>
    Task<AttendanceRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every record dated in [from, to] (payroll), not tracked.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListInRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Records of an employee and of the workers linked to that employee in [from, to], with zone, oldest first.</summary>
    Task<IReadOnlyList<AttendanceRecord>> ListForEmployeeAsync(
        Guid employeeId, IReadOnlyCollection<Guid> workerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    void Add(AttendanceRecord record);
}
