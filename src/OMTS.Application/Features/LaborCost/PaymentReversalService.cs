using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-09 entry errors: a payment is reversed by a negative payment linked to it (reason required), only while its payslip
/// is not Fully Paid (final); an advance only while no finalized payroll deducted any of it. Receipt or signature photos
/// can be added to a payment (up to 5).
/// </summary>
public sealed class PaymentReversalService(
    ILaborPaymentRepository payments,
    IPayrollRepository payrolls,
    PayslipViews views,
    LaborEvidence evidence,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string DocumentCategory = "labor-payment-documents";

    public async Task<Result<PaymentRecorded>> ReverseAsync(Guid paymentId, ReversePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        var invalid = new List<string>();
        var reason = LaborInputRules.Required(request.Reason, LaborInputRules.ReasonLength, "Reason", invalid);
        if (reason is null)
        {
            return AppError.InvalidFields(invalid);
        }

        if (payment.OriginalPaymentId is not null)
        {
            return CannotChange("Payment", "Reversal", "Reversed", "a reversal cannot be reversed");
        }

        var siblings = payment.Settlement?.Payments.ToList()
            ?? (await payments.ListAdvancesOfAsync(payment.EmployeeId is { } e ? [e] : [], payment.WorkerId is { } w ? [w] : [], cancellationToken)).ToList();
        if (siblings.Any(p => p.OriginalPaymentId == payment.Id))
        {
            return CannotChange("Payment", "Reversed", "Reversed", "the payment has already been reversed");
        }

        if (payment.Settlement is { StatusId: StatusIds.LaborSettlement.Paid })
        {
            return CannotChange("Payslip", "Fully Paid", "Approved", "Fully Paid is final");
        }

        if (payment.IsAdvance && await AdvanceRemainingAsync(payment, siblings, cancellationToken) < payment.AmountPaid)
        {
            return CannotChange("Salary advance", "Deducted", "Reversed", "it has already been deducted by a finalized payroll");
        }

        var reversal = new LaborPayment
        {
            SettlementId = payment.SettlementId, EmployeeId = payment.EmployeeId, WorkerId = payment.WorkerId, IsAdvance = payment.IsAdvance,
            Recipient = payment.Recipient, AmountPaid = -payment.AmountPaid, PaymentDate = timeProvider.Today(), Method = payment.Method,
            ReferenceCode = payment.ReferenceCode, OriginalPaymentId = payment.Id, AdjustmentReason = reason,
        };
        payments.Add(reversal);
        if (payment.Settlement is { } payslip)
        {
            unitOfWork.MarkChanged(payslip);
        }

        audit.Record(new AuditEntry("LaborPayment.Reversed", nameof(LaborPayment))
        {
            RecordId = payment.Id, Reason = reason, After = new { ReversalId = reversal.Id, reversal.AmountPaid, reversal.PaymentDate },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var paid = payment.SettlementId is { } payslipId ? await payrolls.GetSettlementAsync(payslipId, cancellationToken) : null;
        var payslipView = paid is null ? null : await views.ViewAsync(paid, paid.Payroll.Period, cancellationToken);
        return new PaymentRecorded(await views.PaymentAsync(reversal, false, cancellationToken), payslipView);
    }

    public async Task<Result<PaymentView>> AddDocumentsAsync(Guid paymentId, IReadOnlyList<IncomingFile> files, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        var stored = await evidence.UploadAsync(files, payment.DocumentUrls.Count, DocumentCategory, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        payment.DocumentUrls = [.. payment.DocumentUrls, .. stored.Value!];
        audit.Record(new AuditEntry("LaborPayment.DocumentsAdded", nameof(LaborPayment))
        {
            RecordId = payment.Id, After = new { Added = stored.Value!.Count, Total = payment.DocumentUrls.Count },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.PaymentAsync(payment, false, cancellationToken);
    }

    private async Task<decimal> AdvanceRemainingAsync(LaborPayment advance, IReadOnlyList<LaborPayment> records, CancellationToken cancellationToken)
    {
        var person = advance.EmployeeId is { } e ? new PayeeKey(PayeeKind.Employee, e) : new PayeeKey(PayeeKind.Worker, advance.WorkerId!.Value);
        var deducted = await payments.GetDeductedAdvanceTotalsAsync(
            advance.EmployeeId is { } employeeId ? [employeeId] : [], advance.WorkerId is { } workerId ? [workerId] : [], cancellationToken);
        return SalaryAdvances.Remaining(records, person, SalaryAdvances.Deducted(deducted, person)).GetValueOrDefault(advance.Id);
    }

    private static AppError CannotChange(string entity, string from, string to, string reason) =>
        AppError.Conflict(UserMessages.CannotChangeStatus(entity, from, to, reason));
}
