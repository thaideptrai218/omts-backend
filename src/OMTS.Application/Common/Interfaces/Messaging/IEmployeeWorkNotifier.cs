using OMTS.Application.Common.Messages;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>MSG-LABOR-05 for one employee: the work of <see cref="Date"/> was assigned, changed or removed.</summary>
public sealed record WorkAssignmentNotice(Guid EmployeeId, DateOnly Date, UserMessage Message);

/// <summary>
/// The push to an employee on the linked device of the employee app (BR-LABOR-04): pushed and not kept, because an employee has no
/// notification list. The employee app comes with LABOR-03 (L3), so the registered implementation sends nothing until L3 or L5
/// replaces it. Called only after the save, and a send that fails changes nothing (GFR-06).
/// </summary>
public interface IEmployeeWorkNotifier
{
    Task NotifyAsync(IReadOnlyList<WorkAssignmentNotice> notices, CancellationToken cancellationToken);
}
