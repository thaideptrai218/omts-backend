using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>What the employee app is told (BR-LABOR-04): an English title and the SRS message, whose code and parameters the app shows in its own language.</summary>
/// <param name="Type">A free label of the kind of push (e.g. "WORK_ASSIGNMENT"); an employee has no notification list, so there is no catalogue of types.</param>
public sealed record EmployeeNotification(
    string Type, string Title, UserMessage Message, string? RelatedRecordType = null, Guid? RelatedRecordId = null, string? RelatedRecordCode = null);

/// <summary>
/// Pushes to an employee of the employee app (BR-LABOR-04: assignments, request results). Push only: employees have no account and no
/// notification list, so nothing is kept. Like the push to an account, it is queued with the caller's unit of work and sent after
/// that save commits; a failed push is logged and undoes nothing (GFR-06).
/// </summary>
public interface IEmployeeNotifier
{
    /// <summary>
    /// Queues the push for the linked device of the employee while one of its sessions is open and it allows notifications. Without
    /// such a device the push is still handed to the provider with no device, as the push to an account is, so the development inbox shows it.
    /// </summary>
    Task NotifyAsync(Guid employeeId, EmployeeNotification notification, CancellationToken cancellationToken);
}
