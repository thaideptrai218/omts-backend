using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking.Dtos;

/// <summary>
/// LAB-03 create/edit body. Exactly one of <see cref="EmployeeId"/>, <see cref="WorkerId"/>, <see cref="TeamId"/>.
/// Piecework needs <see cref="PieceUnit"/>, <see cref="PlannedQuantity"/> and <see cref="PieceRate"/>.
/// </summary>
/// <param name="ConfirmDuplicate">Resend true after the MSG49 warning to save anyway.</param>
/// <param name="RowVersion">Edit only: the version read (MSG06).</param>
public sealed record SaveWorkAssignmentRequest(
    DateOnly? WorkDate,
    Guid? EmployeeId,
    Guid? WorkerId,
    Guid? TeamId,
    string? Task,
    string? Notes,
    WorkPaymentType? PaymentType,
    string? PieceUnit = null,
    decimal? PlannedQuantity = null,
    decimal? PieceRate = null,
    DateOnly? ExpectedCompletionDate = null,
    Guid? RaftId = null,
    Guid? CycleId = null,
    bool ConfirmDuplicate = false,
    string? RowVersion = null);

public sealed record CancelWorkAssignmentRequest(string? Reason);

/// <param name="Reason">Required when completion was already confirmed (re-confirmation).</param>
public sealed record ConfirmCompletionRequest(decimal? CompletedQuantity, DateOnly? CompletionDate, string? CompletionNote, string? Reason = null);

/// <param name="Kind">Employee, Worker or Team.</param>
public sealed record AssigneeRef(string Kind, Guid Id, string Name);

/// <param name="PieceworkAmount">CompletedQuantity × PieceRate rounded up to 1,000 VND.</param>
/// <param name="EvidenceUrls">Short-lived read URLs of the evidence photos.</param>
public sealed record CompletionView(
    decimal CompletedQuantity, DateOnly CompletionDate, string? Note, decimal PieceworkAmount, DateTimeOffset ConfirmedAt, IReadOnlyList<string> EvidenceUrls);

public sealed record CancellationView(DateTimeOffset CancelledAt, string? Reason);

/// <param name="State">Active, Cancelled or Completed (piecework completion confirmed).</param>
public sealed record WorkAssignmentView(
    Guid Id,
    Guid ZoneId,
    string ZoneName,
    Guid? RaftId,
    Guid? CycleId,
    DateOnly WorkDate,
    AssigneeRef Assignee,
    string Task,
    string? Notes,
    WorkPaymentType PaymentType,
    string? PieceUnit,
    decimal? PlannedQuantity,
    decimal? PieceRate,
    DateOnly? ExpectedCompletionDate,
    string State,
    CompletionView? Completion,
    CancellationView? Cancellation,
    string RowVersion);
