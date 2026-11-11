using OMTS.Application.Common.Interfaces.Messaging;

namespace OMTS.Infrastructure.Messaging;

/// <summary>
/// LABOR-03 seam (L3): the employee app, its linked device and its push token do not exist yet, so no employee has a device to
/// push MSG-LABOR-05 to. L3 or L5 replaces this with the push to the linked device of the employee (BR-LABOR-04).
/// </summary>
internal sealed class NoEmployeePushYetNotifier : IEmployeeWorkNotifier
{
    public Task NotifyAsync(IReadOnlyList<WorkAssignmentNotice> notices, CancellationToken cancellationToken) => Task.CompletedTask;
}
