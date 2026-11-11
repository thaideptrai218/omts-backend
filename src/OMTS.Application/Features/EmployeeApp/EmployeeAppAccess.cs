using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>
/// Ends the access of an employee to the employee app (BR-LABOR-02): every open session is revoked and the linked device is
/// unlinked, so a new login code is needed; a phone number change or an end of employment also cancels the open login codes. Used when
/// another device is linked, when the phone number of the profile changes, when the employment ends and when a copy of the app is
/// detected. Logging out is not this: it ends one session and keeps the device.
/// </summary>
public sealed class EmployeeAppAccess(IEmployeeAppRepository app, AuditLogService audit, TimeProvider timeProvider)
{
    /// <summary>Employed on <paramref name="today"/>: the status Employed and a last working day, if any, not before today.</summary>
    public static bool IsEmployedToday(Employee employee, DateOnly today) =>
        employee.StatusId == StatusIds.Employee.Working && (employee.LastWorkingDay is null || employee.LastWorkingDay >= today);

    /// <summary>
    /// Revokes the sessions and unlinks the device in the caller's unit of work, with one audit row when a device was linked; nothing is
    /// saved here, so the change commits with the one that caused it. Does nothing for an employee who never used the app.
    /// </summary>
    /// <param name="actorEmployeeId">The employee, when the employee app itself caused it (a login on a new device); otherwise the audit actor is the caller of the request, or the system.</param>
    public async Task RevokeAsync(
        Guid employeeId, EmployeeDeviceUnlinkReason reason, CancellationToken cancellationToken, Guid? actorEmployeeId = null)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in await app.ListOpenSessionsAsync(employeeId, cancellationToken))
        {
            session.Revoke(now, reason.ToString());
        }

        // An open code outlives nothing that ends the access: a code issued for the old phone number must not link a device on the new
        // one, and one issued before an end of employment must not work again after a resume. Not for a new device (that code is the one
        // being spent) nor for a suspected copy (a code issued since is the way back in).
        if (reason is EmployeeDeviceUnlinkReason.PhoneChanged or EmployeeDeviceUnlinkReason.EmploymentEnded)
        {
            foreach (var code in await app.ListOpenCodesAsync(employeeId, cancellationToken))
            {
                code.CancelledAt = now;
            }
        }

        if (await app.GetLinkedDeviceAsync(employeeId, cancellationToken) is not { } device)
        {
            return;
        }

        var before = new { device.Platform, device.DeviceName, device.LinkedAt };
        device.Unlink(now, reason);
        audit.Record(new AuditEntry("Employee.DeviceUnlinked", nameof(Employee))
        {
            RecordId = employeeId,
            Before = before,
            After = new { UnlinkReason = reason },
            Reason = reason switch
            {
                EmployeeDeviceUnlinkReason.NewDevice => "A login code was entered on another device.",
                EmployeeDeviceUnlinkReason.PhoneChanged => "The phone number of the profile changed.",
                EmployeeDeviceUnlinkReason.CloneSuspected => "A device secret that was already replaced was presented again: the app was probably copied to another phone.",
                _ => "The employment ended.",
            },
            ActorEmployeeId = actorEmployeeId,
        });
    }
}
