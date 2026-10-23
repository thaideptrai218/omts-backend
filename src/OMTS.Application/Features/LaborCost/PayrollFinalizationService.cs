using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-08 – the Cooperative Owner finalizes the draft payroll (every payslip Draft → Approved, advances deducted, the
/// month locked), sends it back with a reason, or reopens a finalized month that has no payment yet.
/// </summary>
public sealed class PayrollFinalizationService(
    IPayrollRepository payrolls,
    ILaborPaymentRepository payments,
    PayrollQueryService queries,
    PayrollBuilder builder,
    PayrollSync sync,
    INotificationDispatcher notifications,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string Approved = "Approved";
    private const string Draft = "Draft";

    public async Task<Result<PayrollDetail>> FinalizeAsync(string periodKey, CancellationToken cancellationToken)
    {
        var payroll = await payrolls.GetAsync(periodKey, cancellationToken);
        if (payroll is null || !PayrollPeriod.TryParse(payroll.Period, out var period))
        {
            return AppError.NotFound;
        }

        if (payroll.IsFinalized)
        {
            return CannotChange(period, Approved, Approved, "the payroll is already finalized");
        }

        // The draft may be older than the attendance, rates and piecework of the month: recompute it first (manual
        // lines are kept), so finalize approves exactly what the sources say today.
        // Saved as a refresh first: replaced lines leave the payslips only once saved, and a refusal below (MSG50)
        // then shows the up-to-date draft.
        sync.Apply(payroll, await builder.ComputeAsync(period, payroll.Settlements.Select(PayslipViews.KeyOf).ToList(), cancellationToken), payroll.PreparedByAccountId);
        unitOfWork.MarkChanged(payroll);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var settlements = payroll.Settlements.ToList();
        var missing = settlements.Where(s => s.MissingRateDates.Count > 0)
            .Select(s => new { Payee = PayslipViews.Payee(s), Dates = s.MissingRateDates.Order().ToList() })
            .ToList();
        if (settlements.Count == 0 || missing.Count > 0)
        {
            return AppError.Conflict(UserMessages.PayBasisIncomplete, new Dictionary<string, object?>
            {
                ["empty"] = settlements.Count == 0,
                ["missing"] = missing,
            });
        }

        var people = settlements.Where(s => s.ContractorId is null).ToList();
        var employeeIds = people.Where(s => s.EmployeeId is not null).Select(s => s.EmployeeId!.Value).ToList();
        var workerIds = people.Where(s => s.WorkerId is not null).Select(s => s.WorkerId!.Value).ToList();
        var advances = await payments.ListAdvancesOfAsync(employeeIds, workerIds, cancellationToken);
        var deducted = await payments.GetDeductedAdvanceTotalsAsync(employeeIds, workerIds, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var settlement in settlements)
        {
            var advanceLine = settlement.PayLines.Where(l => l.LineType == PayLineType.AdvanceDeduction).Sum(l => l.Amount);
            if (advanceLine > 0 && MarkDeductedAdvances(settlement, advances, deducted, advanceLine) is { } stale)
            {
                return stale;
            }

            (settlement.StatusId, settlement.ApprovedByAccountId, settlement.ApprovedAt) = (StatusIds.LaborSettlement.Approved, currentUser.AccountId, now);
            audit.RecordStatusChange(nameof(LaborSettlement), settlement.Id, payroll.Period, StatusIds.LaborSettlement.Draft, StatusIds.LaborSettlement.Approved);
        }

        (payroll.FinalizedAt, payroll.FinalizedByAccountId) = (now, currentUser.AccountId);
        (payroll.ReturnReason, payroll.ReturnedAt, payroll.ReturnedByAccountId) = (null, null, null);
        audit.Record(new AuditEntry("Payroll.Finalized", nameof(Payroll))
        {
            RecordId = payroll.Id, RecordCode = payroll.Period, After = new { Payslips = settlements.Count, Net = settlements.Sum(s => s.TotalObligation) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(payroll, cancellationToken);
    }

    public async Task<Result<PayrollDetail>> SendBackAsync(string periodKey, PayrollReasonRequest request, CancellationToken cancellationToken)
    {
        var payroll = await payrolls.GetAsync(periodKey, cancellationToken);
        if (payroll is null || !PayrollPeriod.TryParse(payroll.Period, out var period))
        {
            return AppError.NotFound;
        }

        if (Reason(request) is not { } reason)
        {
            return AppError.Invalid("Reason");
        }

        if (payroll.IsFinalized)
        {
            return CannotChange(period, Approved, PayslipViews.SentBack, "the payroll is already finalized");
        }

        (payroll.ReturnReason, payroll.ReturnedAt, payroll.ReturnedByAccountId) = (reason, timeProvider.GetUtcNow(), currentUser.AccountId);
        unitOfWork.MarkChanged(payroll);
        audit.Record(new AuditEntry("Payroll.SentBack", nameof(Payroll)) { RecordId = payroll.Id, RecordCode = payroll.Period, Reason = reason });
        var text = $"Payroll {period.Label} was sent back: {reason}";
        await notifications.NotifyAsync(
            [payroll.PreparedByAccountId], NotificationTypes.PayrollSentBack, $"Payroll {period.Label} was sent back", text,
            nameof(Payroll), payroll.Period, isMandatory: false, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(payroll, cancellationToken);
    }

    /// <summary>Approved → Draft for every payslip (reason required), only while no payment of the month is recorded.</summary>
    public async Task<Result<PayrollDetail>> ReopenAsync(string periodKey, PayrollReasonRequest request, CancellationToken cancellationToken)
    {
        var payroll = await payrolls.GetAsync(periodKey, cancellationToken);
        if (payroll is null || !PayrollPeriod.TryParse(payroll.Period, out var period))
        {
            return AppError.NotFound;
        }

        if (Reason(request) is not { } reason)
        {
            return AppError.Invalid("Reason");
        }

        if (!payroll.IsFinalized)
        {
            return CannotChange(period, Draft, Draft, "the payroll is not finalized");
        }

        if (payroll.Settlements.Any(s => s.Payments.Count > 0))
        {
            return CannotChange(period, Approved, Draft, "payments have already been recorded");
        }

        var ids = payroll.Settlements.Select(s => s.Id).ToHashSet();
        var people = payroll.Settlements.Where(s => s.ContractorId is null).ToList();
        var advances = await payments.ListAdvancesOfAsync(
            people.Where(s => s.EmployeeId is not null).Select(s => s.EmployeeId!.Value).ToList(),
            people.Where(s => s.WorkerId is not null).Select(s => s.WorkerId!.Value).ToList(), cancellationToken);
        foreach (var advance in advances.Where(a => a.DeductedInSettlementId is { } id && ids.Contains(id)))
        {
            advance.DeductedInSettlementId = null;
        }

        foreach (var settlement in payroll.Settlements.Where(s => s.StatusId == StatusIds.LaborSettlement.Approved))
        {
            audit.RecordStatusChange(nameof(LaborSettlement), settlement.Id, payroll.Period, settlement.StatusId, StatusIds.LaborSettlement.Draft, reason);
            (settlement.StatusId, settlement.ApprovedByAccountId, settlement.ApprovedAt) = (StatusIds.LaborSettlement.Draft, null, null);
        }

        (payroll.FinalizedAt, payroll.FinalizedByAccountId) = (null, null);
        audit.Record(new AuditEntry("Payroll.Reopened", nameof(Payroll)) { RecordId = payroll.Id, RecordCode = payroll.Period, Reason = reason });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(payroll, cancellationToken);
    }

    /// <summary>
    /// Marks the advances this payslip finishes deducting (oldest first). A deduction larger than what is still outstanding
    /// (another payroll deducted the same advance meanwhile) refuses the finalization until the draft is refreshed.
    /// </summary>
    private static AppError? MarkDeductedAdvances(LaborSettlement settlement, IReadOnlyList<LaborPayment> advances, DeductedAdvanceTotals deducted, decimal deduction)
    {
        var person = PayslipViews.KeyOf(settlement);
        var mine = SalaryAdvances.Of(advances, person);
        var already = SalaryAdvances.Deducted(deducted, person);
        var outstanding = AdvanceLedger.OutstandingUpTo(SalaryAdvances.Entries(mine), already, settlement.PeriodEnd);
        if (deduction > outstanding)
        {
            return AppError.Conflict(
                UserMessages.ExceedsLimit($"Advance deduction of {PayslipViews.Payee(settlement).Name}", VndAmounts.Format(outstanding)),
                new Dictionary<string, object?> { ["payslipId"] = settlement.Id, ["limit"] = outstanding });
        }

        var remaining = SalaryAdvances.Remaining(mine, person, already + deduction);
        foreach (var advance in mine.Where(a => a.DeductedInSettlementId is null && remaining.TryGetValue(a.Id, out var left) && left == 0))
        {
            advance.DeductedInSettlementId = settlement.Id;
        }

        return null;
    }

    private static string? Reason(PayrollReasonRequest request)
    {
        var invalid = new List<string>();
        return LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", invalid);
    }

    private static AppError CannotChange(PayrollPeriod period, string from, string to, string reason) =>
        AppError.Conflict(UserMessages.CannotChangeStatus($"Payroll {period.Label}", from, to, reason));
}
