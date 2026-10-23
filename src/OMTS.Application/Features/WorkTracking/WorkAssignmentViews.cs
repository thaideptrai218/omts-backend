using OMTS.Application.Common.Concurrency;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>Builds the LAB-03/LAB-05 views; the assignee navigation of the assignment must be loaded.</summary>
public sealed class WorkAssignmentViews(LaborEvidence evidence)
{
    public const string Active = "Active";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";

    public static string StateOf(WorkAssignment a) => a.IsCancelled ? Cancelled : a.IsCompleted ? Completed : Active;

    /// <summary>CompletedQuantity × PieceRate rounded up to 1,000 VND (LAB-05).</summary>
    public static decimal PieceworkAmount(WorkAssignment a) =>
        VndAmounts.RoundUpToThousand((a.CompletedQuantity ?? 0) * (a.PieceRate ?? 0));

    public static AssigneeRef Assignee(WorkAssignment a) =>
        a.Employee is { } e ? new AssigneeRef("Employee", e.Id, e.FullName)
        : a.Worker is { } w ? new AssigneeRef("Worker", w.Id, w.FullName)
        : new AssigneeRef("Team", a.ContractorId!.Value, a.Contractor?.Name ?? string.Empty);

    public async Task<WorkAssignmentView> ViewAsync(WorkAssignment a, string zoneName, CancellationToken cancellationToken)
    {
        CompletionView? completion = null;
        if (a is { IsCompleted: true, CompletedQuantity: { } quantity, CompletionDate: { } date, CompletionConfirmedAt: { } confirmedAt })
        {
            completion = new CompletionView(
                quantity, date, a.CompletionNote, PieceworkAmount(a), confirmedAt,
                await evidence.ReadUrlsAsync(a.CompletionEvidenceUrls, cancellationToken));
        }

        return new WorkAssignmentView(
            a.Id, a.ZoneId, zoneName, a.RaftId, a.CycleId, a.WorkDate, Assignee(a), a.Task, a.Notes, a.PaymentType,
            a.PieceUnit, a.PlannedQuantity, a.PieceRate, a.ExpectedCompletionDate, StateOf(a), completion,
            a.CancelledAt is { } cancelledAt ? new CancellationView(cancelledAt, a.CancellationReason) : null,
            RowVersionGuard.Encode(a.RowVersion));
    }

    public static object Snapshot(WorkAssignment a) => new
    {
        a.WorkDate, a.EmployeeId, a.WorkerId, TeamId = a.ContractorId, a.Task, a.Notes, PaymentType = a.PaymentType.ToString(),
        a.PieceUnit, a.PlannedQuantity, a.PieceRate, a.ExpectedCompletionDate, a.RaftId, a.CycleId,
    };
}
