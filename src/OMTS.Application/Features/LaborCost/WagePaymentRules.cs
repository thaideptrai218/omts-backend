using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>Field and payslip rules of LAB-09 payments and advances (MSG01, MSG41, MSG45, MSG61, MSG03).</summary>
public static class WagePaymentRules
{
    public const string AmountField = "Amount paid";

    /// <summary>MSG01 for the shared fields (date not in the future), then MSG41 when the amount is not a multiple of 1,000.</summary>
    public static AppError? CheckFields(
        decimal? amount, DateOnly? date, bool hasMethod, string? referenceCode, string? recipient, string? notes, DateOnly today, string amountField = AmountField)
    {
        var invalid = new List<string>();
        if (amount is not > 0)
        {
            invalid.Add(amountField);
        }

        if (date is null || date > today)
        {
            invalid.Add("Payment date");
        }

        if (!hasMethod)
        {
            invalid.Add("Method");
        }

        LaborInputRules.Optional(referenceCode, 50, "Reference code", invalid);
        LaborInputRules.Optional(recipient, 100, "Recipient", invalid);
        LaborInputRules.Optional(notes, LaborInputRules.NotesLength, "Notes", invalid);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        return VndAmounts.IsThousands(amount!.Value)
            ? null
            : new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = amountField });
    }

    /// <summary>MSG61 for a Draft or already settled payslip, MSG45 when the amount is above the remaining.</summary>
    public static AppError? CheckPayable(LaborSettlement payslip, decimal amount)
    {
        if (payslip.StatusId == StatusIds.LaborSettlement.Draft)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus("Payslip", "Draft", "Fully Paid", "the payroll is not finalized"));
        }

        var remaining = PayslipViews.Remaining(payslip);
        return amount > remaining
            ? AppError.Conflict(UserMessages.ExceedsLimit(AmountField, VndAmounts.Format(remaining)), new Dictionary<string, object?> { ["remaining"] = remaining })
            : null;
    }

    /// <summary>A saved, not reversed payment of the payslip with the same amount, date and reference (MSG03).</summary>
    public static LaborPayment? FindDuplicate(LaborSettlement payslip, decimal amount, DateOnly date, string? referenceCode)
    {
        var reference = Normalize(referenceCode);
        var reversed = payslip.Payments.Where(p => p.OriginalPaymentId is not null).Select(p => p.OriginalPaymentId!.Value).ToHashSet();
        return payslip.Payments.FirstOrDefault(p => p.OriginalPaymentId is null && !reversed.Contains(p.Id)
            && p.AmountPaid == amount && p.PaymentDate == date && Normalize(p.ReferenceCode) == reference);
    }

    /// <summary>Approved → Fully Paid when the payments now cover the net pay.</summary>
    public static bool BecomesFullyPaid(LaborSettlement payslip, decimal newPayment) =>
        payslip.StatusId == StatusIds.LaborSettlement.Approved && PayslipViews.Paid(payslip) + newPayment >= payslip.TotalObligation;

    private static string? Normalize(string? reference) => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim().ToUpperInvariant();
}
