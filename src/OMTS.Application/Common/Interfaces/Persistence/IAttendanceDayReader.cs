namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The check-in and the check-out of an employee on a date; either is null while not recorded (BR-LABOR-06).</summary>
public sealed record DayAttendance(Guid EmployeeId, DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt)
{
    public bool IsCheckedIn => CheckInAt is not null;

    /// <summary>Checked in and not checked out: the state Change job for crew needs (LABOR-02.1).</summary>
    public bool IsAtWork => CheckInAt is not null && CheckOutAt is null;

    /// <summary>Both times are there and in order, so the hours of the day count (BR-LABOR-05, BR-LABOR-06).</summary>
    public bool IsComplete => CheckInAt is { } checkIn && CheckOutAt is { } checkOut && checkOut > checkIn;
}

/// <summary>
/// What work assignment needs from attendance (LABOR-04), behind a port so LABOR-04 (L5) fills it without touching LABOR-02:
/// the check-in decides whether an assignment can still be edited, moved or removed (BR-LABOR-03). Until the Attendance table
/// exists nobody is checked in.
/// </summary>
public interface IAttendanceDayReader
{
    /// <summary>The attendance of these employees on the date, for those who have a record; the others are not in the answer.</summary>
    Task<IReadOnlyDictionary<Guid, DayAttendance>> ReadAsync(DateOnly date, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken);
}
