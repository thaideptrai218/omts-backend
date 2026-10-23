using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>Payslip and payroll views (LAB-07…LAB-10); the payslip must come with its lines, payments and payee.</summary>
public sealed class PayslipViews(LaborEvidence evidence, IStatusCatalog statusCatalog)
{
    public const string Draft = "Draft";
    public const string SentBack = "Sent back";
    public const string Finalized = "Finalized";
    public const string FullyPaid = "Fully paid";

    public static PayeeRef Payee(LaborSettlement s) =>
        s.EmployeeId is { } e ? new PayeeRef("Employee", e, s.Employee?.FullName ?? string.Empty)
        : s.WorkerId is { } w ? new PayeeRef("Worker", w, s.Worker?.FullName ?? string.Empty)
        : new PayeeRef("Team", s.ContractorId!.Value, s.Contractor?.Name ?? string.Empty);

    public static PayeeKey KeyOf(LaborSettlement s) =>
        s.EmployeeId is { } e ? new PayeeKey(PayeeKind.Employee, e)
        : s.WorkerId is { } w ? new PayeeKey(PayeeKind.Worker, w)
        : new PayeeKey(PayeeKind.Team, s.ContractorId!.Value);

    /// <summary>Default recipient of a payment: the payee, or the crew leader for a team payslip.</summary>
    public static string RecipientOf(LaborSettlement s) =>
        s.Employee?.FullName ?? s.Worker?.FullName ?? s.Contractor?.Leader?.FullName ?? s.Contractor?.Name ?? string.Empty;

    public static decimal Paid(LaborSettlement s) => s.Payments.Sum(p => p.AmountPaid);

    public static decimal Remaining(LaborSettlement s) => Math.Max(0, s.TotalObligation - Paid(s));

    /// <summary>Fully Paid, or Approved with nothing left to pay (net 0).</summary>
    public static bool IsSettled(LaborSettlement s) =>
        s.StatusId == StatusIds.LaborSettlement.Paid || (s.StatusId == StatusIds.LaborSettlement.Approved && Remaining(s) == 0);

    public static string StateOf(Payroll p) =>
        !p.IsFinalized ? (p.ReturnReason is null ? Draft : SentBack)
        : p.Settlements.All(IsSettled) ? FullyPaid
        : Finalized;

    public static PayslipTotals TotalsOf(LaborSettlement s) => PayslipBalance.Of(s.PayLines.Select(l => (l.LineType, l.Amount)));

    public static PayrollTotals Totals(IReadOnlyCollection<LaborSettlement> payslips)
    {
        var totals = payslips.Select(TotalsOf).ToList();
        return new PayrollTotals(
            payslips.Count, totals.Sum(t => t.Earnings), totals.Sum(t => t.Deductions), totals.Sum(t => t.AdvanceDeduction),
            payslips.Sum(s => s.TotalObligation), payslips.Sum(Paid));
    }

    public async Task<PayslipView> ViewAsync(LaborSettlement s, string period, CancellationToken cancellationToken)
    {
        var totals = TotalsOf(s);
        var payments = new List<PaymentView>();
        foreach (var payment in s.Payments.OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt))
        {
            payments.Add(await PaymentAsync(payment, s.Payments.Any(p => p.OriginalPaymentId == payment.Id), cancellationToken));
        }

        return new PayslipView(
            s.Id, period, Payee(s), s.StatusId, statusCatalog.DisplayNameOf(s.StatusId), totals.Earnings, totals.Deductions,
            totals.AdvanceDeduction, s.TotalObligation, Paid(s), Remaining(s), s.MissingRateDates.Count > 0,
            s.MissingRateDates.Order().ToList(),
            s.PayLines.OrderBy(l => l.IsManual).ThenBy(l => l.LineType).ThenBy(l => l.PayDate).ThenBy(l => l.Description)
                .Select(l => new PayLineView(l.Id, l.LineType, l.Description, l.PayDate, l.Quantity, l.UnitRate, l.Amount, l.IsManual, l.Reason))
                .ToList(),
            payments, RowVersionGuard.Encode(s.RowVersion));
    }

    public async Task<PaymentView> PaymentAsync(LaborPayment p, bool reversed, CancellationToken cancellationToken) => new(
        p.Id, p.Recipient, p.AmountPaid, p.PaymentDate, p.Method, p.ReferenceCode, p.Notes, p.OriginalPaymentId, p.AdjustmentReason,
        reversed, await evidence.ReadUrlsAsync(p.DocumentUrls, cancellationToken));
}
