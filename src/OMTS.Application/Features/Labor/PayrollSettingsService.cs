using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Labor;

/// <param name="PayPeriodStartDay">1 to 28.</param>
/// <param name="RowVersion">The version the screen was read from.</param>
public sealed record SavePayrollSettingsRequest(int? PayPeriodStartDay, bool? AutomaticPayroll, string? RowVersion);

/// <param name="NextPeriods">The next two pay periods under these settings.</param>
public sealed record PayrollSettingsView(int PayPeriodStartDay, bool AutomaticPayroll, IReadOnlyList<PayPeriod> NextPeriods, string RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record PayrollSettingsSaved(PayrollSettingsView Settings, string Message, string MessageCode);

/// <param name="Message">MSG-LABOR-36: the question to ask before saving.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record PayrollSettingsPreview(int PayPeriodStartDay, bool AutomaticPayroll, IReadOnlyList<PayPeriod> NextPeriods, string Message, string MessageCode);

/// <summary>
/// LABOR-16: the Cooperative Owner sets the pay period start day and automatic payroll. Payrolls already created keep their
/// periods; a new start day applies from the first day after the last of them (BR-LABOR-13). The daily check of automatic
/// payroll comes with LABOR-12 (GFR-07).
/// </summary>
public sealed class PayrollSettingsService(
    ICooperativeRepository cooperatives,
    IPayrollRepository payrolls,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<PayrollSettingsView>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative ? await ViewAsync(cooperative, cancellationToken) : AppError.NotFound;

    /// <summary>Steps 2 and 3: the next two pay periods under the settings the Owner is choosing; left out, what is saved.</summary>
    public async Task<Result<PayrollSettingsPreview>> PreviewAsync(int? startDay, bool? automaticPayroll, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var day = startDay ?? cooperative.PayPeriodStartDay;
        if (!PayPeriodCalendar.IsStartDay(day))
        {
            return AppError.Invalid(StartDayField);
        }

        var automatic = automaticPayroll ?? cooperative.AutomaticPayroll;
        var periods = await NextPeriodsAsync(day, cancellationToken);
        var question = UserMessages.NextPayPeriods(periods[0].Describe(), periods[1].Describe(), automatic);
        return new PayrollSettingsPreview(day, automatic, periods, question.Text, question.Code);
    }

    public async Task<Result<PayrollSettingsSaved>> SaveAsync(SavePayrollSettingsRequest request, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        check.Check(PayPeriodCalendar.IsStartDay(request.PayPeriodStartDay), StartDayField);
        check.Check(request.AutomaticPayroll is not null, "Automatic payroll");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(cooperative);
        cooperative.PayPeriodStartDay = request.PayPeriodStartDay!.Value;
        cooperative.AutomaticPayroll = request.AutomaticPayroll!.Value;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(cooperative));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("PayrollSettings.Changed", nameof(Cooperative)) { RecordId = cooperative.Id, Before = changedBefore, After = changedAfter });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var saved = UserMessages.Saved("Payroll settings");
        return new PayrollSettingsSaved(await ViewAsync(cooperative, cancellationToken), saved.Text, saved.Code);
    }

    private const string StartDayField = "Pay period start day";

    private async Task<PayrollSettingsView> ViewAsync(Cooperative cooperative, CancellationToken cancellationToken) => new(
        cooperative.PayPeriodStartDay,
        cooperative.AutomaticPayroll,
        await NextPeriodsAsync(cooperative.PayPeriodStartDay, cancellationToken),
        RowVersionGuard.Encode(cooperative.RowVersion));

    private async Task<IReadOnlyList<PayPeriod>> NextPeriodsAsync(int startDay, CancellationToken cancellationToken) =>
        PayPeriodCalendar.NextTwo(await payrolls.LastPeriodEndAsync(cancellationToken), timeProvider.Today(), startDay);

    private static object Snapshot(Cooperative c) => new { c.PayPeriodStartDay, c.AutomaticPayroll };
}
