using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Dtos;

/// <param name="Message">MSG08 text when the list is empty, else null.</param>
public sealed record MyList<T>(IReadOnlyList<T> Items, string? Message);

public sealed record MyAttendanceItem(DateOnly Date, string ZoneName, AttendanceResult Result, decimal? Hours, string? Notes);

/// <summary>Finalized payslip of the caller (Approved or Fully Paid); Draft figures are never returned.</summary>
public sealed record MyPayslipListItem(Guid Id, string Period, string Status, decimal Gross, decimal Deductions, decimal AdvanceDeduction, decimal Net, decimal Paid, decimal Remaining);
