namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>The attendance QR code of an employee, valid for <see cref="IAttendanceQrCodes.Lifetime"/> (BR-LABOR-06).</summary>
/// <param name="Code">The text the employee app draws as a QR code.</param>
public sealed record AttendanceQrCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// The attendance QR code that identifies an employee for a check-in or a check-out until it is replaced (LABOR-07, BR-LABOR-06): signed, so
/// it cannot be made up, and valid for 60 seconds, so a photo of it cannot be used later. It holds no secret of the employee.
/// </summary>
public interface IAttendanceQrCodes
{
    /// <summary>How long a code works: the app shows a new one this often.</summary>
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    AttendanceQrCode Issue(Guid employeeId);

    /// <summary>The employee of a code that is genuine and not expired; null otherwise.</summary>
    Guid? Read(string? code);
}
