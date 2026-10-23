using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost.Dtos;

/// <summary>Allowance or deduction of a rate: amount &gt; 0 and a multiple of 1,000; deductions are PerMonth only.</summary>
public sealed record PayComponentInput(PayComponentKind? Kind, string? Name, decimal? Amount, PayComponentBasis? Basis);

/// <summary>The rate itself (LAB-06); also the shared body of a bulk apply.</summary>
/// <param name="Amount">Monthly salary, or the rate per paid day for DailyWage; &gt; 0, multiple of 1,000.</param>
/// <param name="StandardWorkingDays">Required for PerWorkingDay: whole number 1–31 (usually 26).</param>
public sealed record PayRateBody(
    SalaryType? SalaryType,
    decimal? Amount,
    int? StandardWorkingDays,
    IReadOnlyList<PayComponentInput>? Components,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

/// <summary>Create (person + rate) or edit (rate + reason + rowVersion; the person cannot change).</summary>
public sealed record SavePayRateRequest(
    Guid? EmployeeId,
    Guid? WorkerId,
    SalaryType? SalaryType,
    decimal? Amount,
    int? StandardWorkingDays,
    IReadOnlyList<PayComponentInput>? Components,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Reason = null,
    string? RowVersion = null)
{
    public PayRateBody Body => new(SalaryType, Amount, StandardWorkingDays, Components, ValidFrom, ValidTo);
}

public sealed record PayPersonRef(Guid? EmployeeId, Guid? WorkerId);

/// <summary>One version per person with the same rate, all or nothing.</summary>
public sealed record BulkPayRateRequest(IReadOnlyList<PayPersonRef>? People, PayRateBody? Rate);

public sealed record PayComponentView(PayComponentKind Kind, string Name, decimal Amount, PayComponentBasis Basis);

/// <param name="Kind">Employee or Worker.</param>
public sealed record PayPersonView(string Kind, Guid Id, string Name);

/// <param name="UsedByFinalizedPeriods">Periods of finalized payslips calculated from this version: it can no longer be edited.</param>
public sealed record PayRateView(
    Guid Id,
    PayPersonView Person,
    SalaryType SalaryType,
    decimal Amount,
    int? StandardWorkingDays,
    IReadOnlyList<PayComponentView> Components,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Status,
    IReadOnlyList<string> UsedByFinalizedPeriods,
    string RowVersion);

/// <param name="ClosedPrevious">The open-ended version closed on ValidFrom − 1 day by this save, if any.</param>
public sealed record PayRateSaved(PayRateView Rate, PayRateView? ClosedPrevious);

public sealed record PayRateSummary(Guid Id, SalaryType SalaryType, decimal Amount, DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>Pay-rate people list: Employed employees and In Use workers with the rate valid today; no personal data.</summary>
public sealed record PayPersonListItem(string Kind, Guid Id, string Name, TeamRef? Team, PayRateSummary? CurrentRate);

/// <summary>Why one person of a bulk request was refused; nothing is saved then.</summary>
public sealed record BulkFailure(int Index, Guid Id, string? Name, string Code, string Message, string? Field);
