namespace OMTS.Application.Common.Interfaces;

/// <summary>
/// The employee calling the employee app, read from the validated employee app token (BR-LABOR-02). All null for every
/// other request, whatever else the token says: a staff token is never an employee, and an employee token is never an
/// account (<see cref="ICurrentUser.AccountId"/> stays null for it).
/// </summary>
public interface ICurrentEmployee
{
    Guid? EmployeeId { get; }
    Guid? SessionId { get; }
    Guid? DeviceId { get; }
}
