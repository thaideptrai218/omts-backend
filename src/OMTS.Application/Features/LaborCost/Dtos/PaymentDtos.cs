using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Dtos;

/// <param name="Recipient">Default: the payee, or the crew leader for a team payslip; at most 100 characters.</param>
/// <param name="AmountPaid">&gt; 0, multiple of 1,000, at most the remaining amount (MSG45).</param>
public sealed record RecordPaymentRequest(
    decimal? AmountPaid, DateOnly? PaymentDate, PaymentMethod? Method, string? ReferenceCode = null, string? Recipient = null, string? Notes = null);

/// <summary>Pays the full remaining amount of every payslip with one date and method; all or nothing.</summary>
public sealed record BulkPaymentRequest(IReadOnlyList<Guid>? PayslipIds, DateOnly? PaymentDate, PaymentMethod? Method, string? ReferenceCode = null);

public sealed record ReversePaymentRequest(string? Reason);

/// <summary>Salary advance to an Employed employee or an In Use worker (exactly one).</summary>
public sealed record RecordAdvanceRequest(
    Guid? EmployeeId,
    Guid? WorkerId,
    decimal? Amount,
    DateOnly? PaymentDate,
    PaymentMethod? Method,
    string? Notes = null,
    string? Recipient = null,
    string? ReferenceCode = null);

/// <summary>A recorded payment and the payslip it paid (absent for advances).</summary>
public sealed record PaymentRecorded(PaymentView Payment, PayslipView? Payslip);

/// <summary>Payable row of <c>GET /api/payslips</c>.</summary>
public sealed record PayableItem(Guid PayslipId, string Period, PayeeRef Payee, int StatusId, string Status, decimal Net, decimal Paid, decimal Remaining);

/// <param name="Remaining">Part not yet deducted by a finalized payroll (0 once deducted or reversed).</param>
/// <param name="DeductedInPeriod">Payroll month that finished deducting it.</param>
public sealed record AdvanceView(
    Guid Id,
    PayeeRef Person,
    decimal Amount,
    decimal Remaining,
    DateOnly PaymentDate,
    PaymentMethod Method,
    string Recipient,
    string? ReferenceCode,
    string? Notes,
    bool Reversed,
    Guid? DeductedInPayslipId,
    string? DeductedInPeriod);

/// <summary>Why one payslip of a bulk payment was refused; nothing is saved then.</summary>
public sealed record BulkPaymentFailure(Guid PayslipId, string Code, string Message);
