using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>
/// LAB-05 – the Zone Manager confirms the piecework quantity actually completed; only confirmed quantity is paid, in the
/// payroll month of the completion date. Re-confirming needs a reason and is allowed until that month is finalized.
/// </summary>
public sealed class PieceworkCompletionService(
    IWorkAssignmentRepository assignments,
    WorkAssignmentViews views,
    LaborEvidence evidence,
    PayrollPeriodLock periodLock,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string EvidenceCategory = "piecework-evidence";

    public async Task<Result<WorkAssignmentView>> ConfirmAsync(Guid zoneId, Guid id, ConfirmCompletionRequest request, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null || assignment.ZoneId != zoneId)
        {
            return AppError.NotFound;
        }

        if (assignment.PaymentType != WorkPaymentType.Piecework || assignment.IsCancelled)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Work assignment", WorkAssignmentViews.StateOf(assignment), WorkAssignmentViews.Completed, "only active piecework can be confirmed"));
        }

        var invalid = new List<string>();
        if (request.CompletedQuantity is not >= 0 || !VndAmounts.HasAtMostTwoDecimals(request.CompletedQuantity.Value))
        {
            invalid.Add("Completed quantity");
        }

        if (request.CompletionDate is not { } date || date < assignment.WorkDate || date > timeProvider.Today())
        {
            invalid.Add("Completion date");
        }

        var note = LaborInputRules.Optional(request.CompletionNote, LaborInputRules.NotesLength, "Completion note", invalid);
        var reason = assignment.IsCompleted
            ? LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", invalid)
            : LaborInputRules.Optional(request.Reason, LaborInputRules.ReasonLength, "Reason", invalid);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var dates = assignment.CompletionDate is { } previous ? new[] { previous, request.CompletionDate!.Value } : [request.CompletionDate!.Value];
        if (await periodLock.CheckAsync(dates, cancellationToken) is { } locked)
        {
            return locked;
        }

        var before = assignment.IsCompleted ? Snapshot(assignment) : null;
        assignment.CompletedQuantity = request.CompletedQuantity;
        assignment.CompletionDate = request.CompletionDate;
        assignment.CompletionNote = note;
        assignment.CompletionConfirmedAt = timeProvider.GetUtcNow();
        assignment.CompletionConfirmedByAccountId = currentUser.AccountId;
        audit.Record(new AuditEntry("WorkAssignment.CompletionConfirmed", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, ZoneId = zoneId, Reason = reason, Before = before, After = Snapshot(assignment),
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(assignment, assignment.Zone.Name, cancellationToken);
    }

    /// <summary>Adds completion evidence photos (up to 5 in all) to confirmed piecework of an open month.</summary>
    public async Task<Result<WorkAssignmentView>> AddEvidenceAsync(Guid zoneId, Guid id, IReadOnlyList<IncomingFile> files, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null || assignment.ZoneId != zoneId)
        {
            return AppError.NotFound;
        }

        if (!assignment.IsCompleted || assignment.IsCancelled)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Work assignment", WorkAssignmentViews.StateOf(assignment), WorkAssignmentViews.Completed, "evidence is added to confirmed piecework"));
        }

        if (await periodLock.CheckAsync(assignment.CompletionDate!.Value, cancellationToken) is { } locked)
        {
            return locked;
        }

        var stored = await evidence.UploadAsync(files, assignment.CompletionEvidenceUrls.Count, EvidenceCategory, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        assignment.CompletionEvidenceUrls = [.. assignment.CompletionEvidenceUrls, .. stored.Value!];
        audit.Record(new AuditEntry("WorkAssignment.EvidenceAdded", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, ZoneId = zoneId, After = new { Added = stored.Value!.Count, Total = assignment.CompletionEvidenceUrls.Count },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(assignment, assignment.Zone.Name, cancellationToken);
    }

    private static object Snapshot(WorkAssignment a) => new
    {
        a.CompletedQuantity, a.CompletionDate, a.CompletionNote, PieceworkAmount = WorkAssignmentViews.PieceworkAmount(a),
    };
}
