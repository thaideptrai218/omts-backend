using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>
/// LAB-03 notifications: an employee with an Active account gets an in-app WORK_ASSIGNMENT notification when work is
/// assigned, rescheduled or cancelled, and a push after the save commits (INotificationDispatcher; a push failure is
/// logged and never blocks the save). Workers and teams have no accounts and get nothing.
/// </summary>
public sealed class WorkAssignmentNotifier(INotificationDispatcher notifications)
{
    public const string AssignedTitle = "New work assignment";
    public const string CancelledTitle = "Work assignment cancelled";

    /// <summary>The employee navigation must carry its account.</summary>
    public Task NotifyAsync(Employee? employee, WorkAssignment assignment, string zoneName, bool cancelled, CancellationToken cancellationToken)
    {
        if (employee?.Account is not { StatusId: StatusIds.Account.Active } account)
        {
            return Task.CompletedTask;
        }

        var body = $"{assignment.Task} – {zoneName} – {LaborInputRules.Date(assignment.WorkDate)}";
        return notifications.NotifyAsync(
            [account.Id], NotificationTypes.WorkAssignment, cancelled ? CancelledTitle : AssignedTitle, body,
            nameof(WorkAssignment), assignment.Id.ToString(), isMandatory: false, cancellationToken);
    }
}
