using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-09 – the Accountant records full or partial payments of finalized payslips (alone or in bulk). A payment equal to
/// the remaining moves the payslip to Fully Paid (final). Payments are never deleted: entry errors are reversed.
/// </summary>
public sealed class WagePaymentService(
    IPayrollRepository payrolls,
    ILaborPaymentRepository payments,
    PayslipViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<PaymentRecorded>> RecordAsync(Guid payslipId, RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var payslip = await payrolls.GetSettlementAsync(payslipId, cancellationToken);
        if (payslip is null)
        {
            return AppError.NotFound;
        }

        var fieldError = WagePaymentRules.CheckFields(
            request.AmountPaid, request.PaymentDate, request.Method is not null, request.ReferenceCode, request.Recipient, request.Notes, timeProvider.Today());
        if (fieldError is not null)
        {
            return fieldError;
        }

        var amount = request.AmountPaid!.Value;
        if (WagePaymentRules.CheckPayable(payslip, amount) is { } refused)
        {
            return refused;
        }

        if (WagePaymentRules.FindDuplicate(payslip, amount, request.PaymentDate!.Value, request.ReferenceCode) is { } duplicate)
        {
            return AppError.AlreadyExists("Payment", duplicate.Id);
        }

        var payment = Pay(payslip, amount, request.PaymentDate.Value, request.Method!.Value, request.ReferenceCode, request.Recipient, request.Notes);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PaymentRecorded(await views.PaymentAsync(payment, false, cancellationToken), await views.ViewAsync(payslip, payslip.Payroll.Period, cancellationToken));
    }

    /// <summary>Pays the full remaining of every payslip; any refused payslip saves nothing and every failure is listed.</summary>
    public async Task<Result<IReadOnlyList<PaymentRecorded>>> BulkAsync(BulkPaymentRequest request, CancellationToken cancellationToken)
    {
        var ids = request.PayslipIds ?? [];
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            return AppError.Invalid("Payslips");
        }

        var invalid = new List<string>();
        if (request.PaymentDate is null || request.PaymentDate > timeProvider.Today())
        {
            invalid.Add("Payment date");
        }

        if (request.Method is null)
        {
            invalid.Add("Method");
        }

        if (request.ReferenceCode?.Trim().Length > 50)
        {
            invalid.Add("Reference code");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var payslips = (await payrolls.GetSettlementsAsync(ids, cancellationToken)).ToDictionary(s => s.Id);
        var failures = new List<(BulkPaymentFailure Failure, AppError Error)>();
        foreach (var id in ids)
        {
            var problem = !payslips.TryGetValue(id, out var payslip) ? AppError.NotFound
                : payslip.StatusId == StatusIds.LaborSettlement.Draft || PayslipViews.Remaining(payslip) == 0
                    ? WagePaymentRules.CheckPayable(payslip, 1) ?? AppError.Conflict(UserMessages.CannotChangeStatus("Payslip", "Fully Paid", "Fully Paid", "nothing is left to pay"))
                    : WagePaymentRules.FindDuplicate(payslip, PayslipViews.Remaining(payslip), request.PaymentDate!.Value, request.ReferenceCode) is { } dup
                        ? AppError.AlreadyExists("Payment", dup.Id)
                        : null;
            if (problem is not null)
            {
                failures.Add((new BulkPaymentFailure(id, problem.Message.Code, problem.Message.Text), problem));
            }
        }

        if (failures.Count > 0)
        {
            var first = failures[0].Error;
            return new AppError(first.Kind, first.Message, new Dictionary<string, object?> { ["failures"] = failures.Select(f => f.Failure).ToList() });
        }

        var paid = ids.Select(id => (Payslip: payslips[id], Payment: Pay(
            payslips[id], PayslipViews.Remaining(payslips[id]), request.PaymentDate!.Value, request.Method!.Value, request.ReferenceCode, null, null))).ToList();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var results = new List<PaymentRecorded>();
        foreach (var (payslip, payment) in paid)
        {
            results.Add(new PaymentRecorded(await views.PaymentAsync(payment, false, cancellationToken), await views.ViewAsync(payslip, payslip.Payroll.Period, cancellationToken)));
        }

        return results;
    }

    /// <summary>Payables: <c>unpaid</c> (default: Approved with an amount left), <c>paid</c> or <c>all</c> finalized payslips.</summary>
    public async Task<Result<IReadOnlyList<PayableItem>>> ListAsync(string? state, string? period, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(state) ? "unpaid" : state.Trim().ToLowerInvariant();
        if (normalized is not ("unpaid" or "paid" or "all"))
        {
            return AppError.Invalid("State");
        }

        if (period is not null && !PayrollPeriod.TryParse(period, out _))
        {
            return AppError.Invalid("Period");
        }

        var payslips = await payrolls.SearchSettlementsAsync(
            new PayslipFilter([StatusIds.LaborSettlement.Approved, StatusIds.LaborSettlement.Paid], period?.Trim()), cancellationToken);
        return payslips
            .Where(s => normalized == "all" || (normalized == "unpaid") != PayslipViews.IsSettled(s))
            .Select(s => new PayableItem(
                s.Id, s.Payroll.Period, PayslipViews.Payee(s), s.StatusId, s.StatusId == StatusIds.LaborSettlement.Paid ? "Fully Paid" : "Approved",
                s.TotalObligation, PayslipViews.Paid(s), PayslipViews.Remaining(s)))
            .ToList();
    }

    public async Task<Result<PayslipView>> GetAsync(Guid payslipId, CancellationToken cancellationToken) =>
        await payrolls.GetSettlementAsync(payslipId, cancellationToken) is { } payslip
            ? await views.ViewAsync(payslip, payslip.Payroll.Period, cancellationToken)
            : AppError.NotFound;

    private LaborPayment Pay(
        LaborSettlement payslip, decimal amount, DateOnly date, PaymentMethod method, string? referenceCode, string? recipient, string? notes)
    {
        var payment = new LaborPayment
        {
            SettlementId = payslip.Id,
            Recipient = string.IsNullOrWhiteSpace(recipient) ? PayslipViews.RecipientOf(payslip) : recipient.Trim(),
            AmountPaid = amount,
            PaymentDate = date,
            Method = method,
            ReferenceCode = string.IsNullOrWhiteSpace(referenceCode) ? null : referenceCode.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
        if (WagePaymentRules.BecomesFullyPaid(payslip, amount))
        {
            audit.RecordStatusChange(nameof(LaborSettlement), payslip.Id, payslip.Payroll.Period, payslip.StatusId, StatusIds.LaborSettlement.Paid);
            payslip.StatusId = StatusIds.LaborSettlement.Paid;
        }

        // Added through the repository: a new entity with its key set is only seen as new when added to its set.
        payments.Add(payment);
        // A parallel payment of the same payslip fails on its row version (MSG06) instead of overpaying.
        unitOfWork.MarkChanged(payslip);
        audit.Record(new AuditEntry("LaborPayment.Created", nameof(LaborPayment))
        {
            RecordId = payment.Id, RecordCode = payslip.Payroll.Period,
            After = new { PayslipId = payslip.Id, payment.Recipient, payment.AmountPaid, payment.PaymentDate, Method = payment.Method.ToString(), payment.ReferenceCode },
        });
        return payment;
    }
}
