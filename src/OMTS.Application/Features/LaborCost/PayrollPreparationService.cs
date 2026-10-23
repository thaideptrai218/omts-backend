using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-07 – the Accountant creates the payroll of a month (filled from attendance, confirmed piecework, pay rates and
/// advances), refreshes it (system lines recalculated, manual lines kept) and adjusts it with manual lines while Draft.
/// </summary>
public sealed class PayrollPreparationService(
    IPayrollRepository payrolls,
    PayrollBuilder builder,
    PayrollSync sync,
    PayrollQueryService queries,
    PayslipViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser)
{
    public async Task<Result<PayrollDetail>> CreateAsync(CreatePayrollRequest request, CancellationToken cancellationToken)
    {
        if (!PayrollPeriod.TryParse(request.Period, out var period))
        {
            return AppError.Invalid("Period");
        }

        if (await payrolls.GetAsync(period.Key, cancellationToken) is { } existing)
        {
            return Duplicate(period, existing);
        }

        var caller = Caller();
        var payroll = new Payroll { Period = period.Key, PeriodStart = period.Start, PeriodEnd = period.End, PreparedByAccountId = caller };
        payrolls.Add(payroll);
        var payslips = sync.Apply(payroll, await builder.ComputeAsync(period, [], cancellationToken), caller);
        audit.Record(new AuditEntry("Payroll.Created", nameof(Payroll)) { RecordId = payroll.Id, RecordCode = period.Key, After = Summary(payroll, payslips) });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // Two Accountants created the month at once: the unique period index keeps the first one.
            var winner = await payrolls.GetAsync(period.Key, cancellationToken);
            return winner is null ? throw new InvalidOperationException("Duplicate payroll without a winner.") : Duplicate(period, winner);
        }

        return await queries.GetAsync(period.Key, cancellationToken);
    }

    public async Task<Result<PayrollDetail>> RefreshAsync(string periodKey, CancellationToken cancellationToken)
    {
        var payroll = await payrolls.GetAsync(periodKey, cancellationToken);
        if (payroll is null || !PayrollPeriod.TryParse(payroll.Period, out var period))
        {
            return AppError.NotFound;
        }

        if (payroll.IsFinalized)
        {
            return PayrollPeriodLock.Locked(period);
        }

        var before = Summary(payroll, payroll.Settlements.ToList());
        var existingPayees = payroll.Settlements.Select(PayslipViews.KeyOf).ToList();
        var payslips = sync.Apply(payroll, await builder.ComputeAsync(period, existingPayees, cancellationToken), Caller());
        unitOfWork.MarkChanged(payroll);
        audit.Record(new AuditEntry("Payroll.Refreshed", nameof(Payroll))
        {
            RecordId = payroll.Id, RecordCode = payroll.Period, Before = before, After = Summary(payroll, payslips),
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.DetailAsync(payroll, cancellationToken);
    }

    /// <summary>Addition or Deduction with a reason on a Draft payslip; the advance deduction is re-capped so net ≥ 0.</summary>
    public async Task<Result<PayslipView>> AddManualLineAsync(string periodKey, Guid payslipId, AddManualLineRequest request, CancellationToken cancellationToken)
    {
        var payroll = await payrolls.GetAsync(periodKey, cancellationToken);
        var settlement = payroll?.Settlements.FirstOrDefault(s => s.Id == payslipId);
        if (payroll is null || settlement is null || !PayrollPeriod.TryParse(payroll.Period, out var period))
        {
            return AppError.NotFound;
        }

        if (payroll.IsFinalized)
        {
            return PayrollPeriodLock.Locked(period);
        }

        var invalid = new List<string>();
        if (request.Type is not (PayLineType.Addition or PayLineType.Deduction))
        {
            invalid.Add("Type");
        }

        if (request.Amount is not > 0)
        {
            invalid.Add("Amount");
        }

        var reason = LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", invalid);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (!VndAmounts.IsThousands(request.Amount!.Value))
        {
            return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = "Amount" });
        }

        var advanceLine = settlement.PayLines.FirstOrDefault(l => l.LineType == PayLineType.AdvanceDeduction);
        var others = settlement.PayLines.Where(l => l != advanceLine).ToList();
        if (request.Type == PayLineType.Deduction)
        {
            var available = Math.Max(0, PayslipBalance.Settle(others.Select(l => (l.LineType, l.Amount)), 0).Net);
            if (request.Amount > available)
            {
                return AppError.Conflict(UserMessages.ExceedsLimit("Deduction amount", VndAmounts.Format(available)),
                    new Dictionary<string, object?> { ["limit"] = available });
            }
        }

        var line = PayrollSync.ToPayLine(settlement, new CalculatedLine(request.Type!.Value, reason!, settlement.PeriodEnd, 1, request.Amount, request.Amount.Value));
        (line.IsManual, line.Reason) = (true, reason);
        payrolls.AddLine(line);
        var outstanding = await builder.OutstandingAdvancesAsync(PayslipViews.KeyOf(settlement), period.End, cancellationToken);
        sync.SetAdvanceAndTotal(settlement, [.. others, line], outstanding, advanceLine);
        unitOfWork.MarkChanged(settlement);
        audit.Record(new AuditEntry("PayLine.Created", nameof(PayLine))
        {
            RecordId = line.Id, RecordCode = payroll.Period, Reason = reason,
            After = new { PayslipId = settlement.Id, Type = line.LineType.ToString(), line.Amount, Net = settlement.TotalObligation },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(settlement, payroll.Period, cancellationToken);
    }

    private static AppError Duplicate(PayrollPeriod period, Payroll existing) =>
        AppError.Conflict(UserMessages.AlreadyExists($"Payroll {period.Label}"),
            new Dictionary<string, object?> { ["existingId"] = existing.Id, ["period"] = existing.Period });

    /// <summary>Before/after figures of the Payroll.Created and Payroll.Refreshed audit entries (also written by finalize).</summary>
    internal static object Summary(Payroll p, IReadOnlyList<LaborSettlement> payslips) => new
    {
        p.Period, Payslips = payslips.Count, Net = payslips.Sum(s => s.TotalObligation),
        MissingRates = payslips.Count(s => s.MissingRateDates.Count > 0),
    };

    private Guid Caller() => currentUser.AccountId ?? throw new InvalidOperationException("Payroll needs an authenticated caller.");
}
