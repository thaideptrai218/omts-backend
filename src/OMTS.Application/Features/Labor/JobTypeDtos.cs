namespace OMTS.Application.Features.Labor;

/// <param name="Name">No other job type has it, whatever the letter case (BR-LABOR-12).</param>
/// <param name="HourlyRate">Whole VND, greater than 0.</param>
/// <param name="EffectiveFrom">After the last day of every pay period whose payroll is Awaiting finalization or Finalized.</param>
public sealed record AddJobTypeRequest(string? Name, decimal? HourlyRate, DateOnly? EffectiveFrom);

/// <param name="HourlyRate">Whole VND, greater than 0.</param>
/// <param name="EffectiveFrom">The date of an existing rate replaces that rate (BR-LABOR-12).</param>
/// <param name="RowVersion">The version of the job type the screen was read from.</param>
public sealed record ChangeRateRequest(decimal? HourlyRate, DateOnly? EffectiveFrom, string? RowVersion);

/// <param name="RowVersion">The version of the job type the screen was read from.</param>
public sealed record RenameJobTypeRequest(string? Name, string? RowVersion);

/// <param name="RowVersion">The version of the job type the screen was read from.</param>
public sealed record JobTypeStatusRequest(string? RowVersion);

public sealed record PayRateView(decimal HourlyRate, DateOnly EffectiveFrom);

/// <param name="CurrentRate">The rate effective today; null while every rate has a later date.</param>
/// <param name="OtherRates">The rates of the other dates, the latest first, also those still to come.</param>
public sealed record JobTypeView(
    Guid Id, string Name, int StatusId, string Status, PayRateView? CurrentRate, IReadOnlyList<PayRateView> OtherRates, string RowVersion);

/// <summary>A job type to choose on a profile or for work: no rate, as a Zone Manager does not use LABOR-11 (§I.4.4).</summary>
public sealed record JobTypeOption(Guid Id, string Name);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record JobTypeSaved(JobTypeView JobType, string Message, string MessageCode);
