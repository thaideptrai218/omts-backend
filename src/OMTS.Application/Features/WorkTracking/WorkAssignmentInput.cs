using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>Field rules of a work assignment (LAB-03 data), checked before any lookup.</summary>
public sealed record WorkAssignmentInput(
    DateOnly WorkDate,
    string Task,
    string? Notes,
    WorkPaymentType PaymentType,
    string? PieceUnit,
    decimal? PlannedQuantity,
    decimal? PieceRate,
    DateOnly? ExpectedCompletionDate)
{
    public const string AssigneeField = "Assignee";

    public static (WorkAssignmentInput? Input, List<string> Invalid) From(SaveWorkAssignmentRequest request)
    {
        var invalid = new List<string>();
        if (request.WorkDate is null)
        {
            invalid.Add("Work date");
        }

        if (new[] { request.EmployeeId, request.WorkerId, request.TeamId }.Count(id => id is not null) != 1)
        {
            invalid.Add(AssigneeField);
        }

        var task = LaborInputRules.Required(request.Task, 500, "Task", invalid);
        var notes = LaborInputRules.Optional(request.Notes, LaborInputRules.NotesLength, "Notes", invalid);
        if (request.PaymentType is null)
        {
            invalid.Add("Payment type");
        }

        string? unit = null;
        if (request.PaymentType == WorkPaymentType.Piecework)
        {
            unit = LaborInputRules.Required(request.PieceUnit, 50, "Piece unit", invalid);
            if (request.PlannedQuantity is not > 0 || !VndAmounts.HasAtMostTwoDecimals(request.PlannedQuantity.Value))
            {
                invalid.Add("Planned quantity");
            }

            if (request.PieceRate is not > 0 || !VndAmounts.IsWhole(request.PieceRate.Value))
            {
                invalid.Add("Piece rate");
            }

            if (request.ExpectedCompletionDate < request.WorkDate)
            {
                invalid.Add("Expected completion date");
            }
        }
        else if (request.PaymentType == WorkPaymentType.DailyWage)
        {
            // Piece fields belong to piecework only.
            AddIfSet(request.PieceUnit, "Piece unit", invalid);
            AddIfSet(request.PlannedQuantity, "Planned quantity", invalid);
            AddIfSet(request.PieceRate, "Piece rate", invalid);
            AddIfSet(request.ExpectedCompletionDate, "Expected completion date", invalid);
        }

        if (request.CycleId is not null && request.RaftId is null)
        {
            invalid.Add("Cycle");
        }

        return invalid.Count > 0
            ? (null, invalid)
            : (new WorkAssignmentInput(
                request.WorkDate!.Value, task!, notes, request.PaymentType!.Value, unit,
                request.PaymentType == WorkPaymentType.Piecework ? request.PlannedQuantity : null,
                request.PaymentType == WorkPaymentType.Piecework ? request.PieceRate : null,
                request.PaymentType == WorkPaymentType.Piecework ? request.ExpectedCompletionDate : null), invalid);
    }

    /// <summary>After piecework completion is confirmed only Task and Notes can change: the first other field that differs.</summary>
    public string? FieldLockedAfterCompletion(WorkAssignment a, SaveWorkAssignmentRequest r) =>
        a.WorkDate != WorkDate ? "Work date"
        : a.EmployeeId != r.EmployeeId || a.WorkerId != r.WorkerId || a.ContractorId != r.TeamId ? AssigneeField
        : a.PaymentType != PaymentType ? "Payment type"
        : a.PieceUnit != PieceUnit ? "Piece unit"
        : a.PlannedQuantity != PlannedQuantity ? "Planned quantity"
        : a.PieceRate != PieceRate ? "Piece rate"
        : a.ExpectedCompletionDate != ExpectedCompletionDate ? "Expected completion date"
        : a.RaftId != r.RaftId ? "Raft"
        : a.CycleId != r.CycleId ? "Cycle"
        : null;

    /// <summary>Writes the fields and the checked assignee (navigation and key together) into the assignment.</summary>
    public void ApplyTo(WorkAssignment a, Employee? employee, SeasonalWorker? worker, Contractor? team, Guid? raftId, Guid? cycleId)
    {
        (a.WorkDate, a.Task, a.Notes, a.PaymentType) = (WorkDate, Task, Notes, PaymentType);
        (a.PieceUnit, a.PlannedQuantity, a.PieceRate, a.ExpectedCompletionDate) = (PieceUnit, PlannedQuantity, PieceRate, ExpectedCompletionDate);
        (a.RaftId, a.CycleId) = (raftId, cycleId);
        (a.Employee, a.EmployeeId) = (employee, employee?.Id);
        (a.Worker, a.WorkerId) = (worker, worker?.Id);
        (a.Contractor, a.ContractorId) = (team, team?.Id);
    }

    private static void AddIfSet(object? value, string field, List<string> invalid)
    {
        if (value is not null && value is not string { Length: 0 })
        {
            invalid.Add(field);
        }
    }
}
