using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// The login codes, the linked device and the sessions of the employee app (LABOR-03, LABOR-06). "Employed today" means the
/// status Employed and no last working day before today: the day after the last working day the employee is Left even
/// before the daily job has recorded it (BR-LABOR-01).
/// </summary>
public interface IEmployeeAppRepository
{
    /// <summary>The employee Employed today who has this normalised phone number, with their type periods; not tracked.</summary>
    Task<Employee?> FindEmployedByPhoneAsync(string phoneNumber, DateOnly today, CancellationToken cancellationToken);

    /// <summary>Codes not consumed and not cancelled, tracked so the caller can cancel them in its save.</summary>
    Task<IReadOnlyList<EmployeeLoginCode>> ListOpenCodesAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>The employee's linked device, tracked; null when none is linked.</summary>
    Task<EmployeeDevice?> GetLinkedDeviceAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>A device by id, tracked, whether linked or not.</summary>
    Task<EmployeeDevice?> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Sessions of the employee not revoked yet (expired ones included, harmless), tracked so the caller can revoke them in its save.</summary>
    Task<IReadOnlyList<EmployeeSession>> ListOpenSessionsAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>Session whose current refresh token has this hash, with its employee (and type periods) and device loaded.</summary>
    Task<EmployeeSession?> GetSessionByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    /// <summary>Session whose previous (just replaced) refresh token has this hash, with its employee (and type periods) and device loaded.</summary>
    Task<EmployeeSession?> GetSessionByPreviousRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    Task<EmployeeSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Checked on every request of the employee app: the session is not revoked and not past its absolute limit, its device is
    /// still linked and the employee is Employed today (GFR-02, BR-LABOR-02).
    /// </summary>
    Task<bool> IsSessionActiveAsync(Guid employeeId, Guid sessionId, Guid deviceId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The employee's linked device while one of its sessions is open: where an employee push is delivered; null otherwise.</summary>
    Task<EmployeeDevice?> GetPushTargetAsync(Guid employeeId, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(EmployeeLoginCode code);

    void Add(EmployeeDevice device);

    void Add(EmployeeSession session);
}
