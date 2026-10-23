using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>LAB-07 reads: months with totals and derived state, and one payroll with its payslips, lines and flags.</summary>
public sealed class PayrollQueryService(IPayrollRepository payrolls, PayslipViews views)
{
    public async Task<IReadOnlyList<PayrollListItem>> ListAsync(int? year, CancellationToken cancellationToken) =>
        (await payrolls.ListAsync(year, cancellationToken))
            .Select(p => new PayrollListItem(
                p.Period, p.PeriodStart, p.PeriodEnd, PayslipViews.StateOf(p), PayslipViews.Totals(p.Settlements.ToList()),
                p.Settlements.Any(s => s.MissingRateDates.Count > 0), p.FinalizedAt))
            .ToList();

    public async Task<Result<PayrollDetail>> GetAsync(string period, CancellationToken cancellationToken) =>
        await payrolls.GetAsync(period, cancellationToken) is { } payroll ? await DetailAsync(payroll, cancellationToken) : AppError.NotFound;

    public async Task<PayrollDetail> DetailAsync(Payroll payroll, CancellationToken cancellationToken)
    {
        var payslips = new List<PayslipView>();
        foreach (var settlement in payroll.Settlements.OrderBy(s => s.ContractorId is null ? 0 : 1).ThenBy(s => PayslipViews.Payee(s).Name))
        {
            payslips.Add(await views.ViewAsync(settlement, payroll.Period, cancellationToken));
        }

        return new PayrollDetail(
            payroll.Period, payroll.PeriodStart, payroll.PeriodEnd, PayslipViews.StateOf(payroll), payroll.ReturnReason, payroll.ReturnedAt,
            payroll.FinalizedAt, PayslipViews.Totals(payroll.Settlements.ToList()), payslips, RowVersionGuard.Encode(payroll.RowVersion));
    }
}
