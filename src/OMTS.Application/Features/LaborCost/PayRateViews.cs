using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.LaborCost;

/// <summary>Pay rate views (LAB-06); the person navigation of the version must be loaded.</summary>
public sealed class PayRateViews(IPayRateRepository rates, IStatusCatalog statusCatalog)
{
    public async Task<IReadOnlyList<PayRateView>> ViewsAsync(IReadOnlyList<PayRuleVersion> versions, CancellationToken cancellationToken)
    {
        var usage = await rates.GetFinalizedPeriodsUsingAsync(versions.Select(v => v.Id).ToList(), cancellationToken);
        return versions.Select(v => View(v, usage.GetValueOrDefault(v.Id) ?? [])).ToList();
    }

    public async Task<PayRateView> ViewAsync(PayRuleVersion version, CancellationToken cancellationToken) =>
        (await ViewsAsync([version], cancellationToken))[0];

    public PayRateView View(PayRuleVersion v, IReadOnlyList<string> usedByPeriods) => new(
        v.Id, Person(v), v.SalaryType, v.Amount, v.StandardWorkingDays,
        v.Components.Select(c => new PayComponentView(c.Kind, c.Name, c.Amount, c.Basis)).ToList(),
        v.ValidFrom, v.ValidTo, statusCatalog.DisplayNameOf(v.StatusId), usedByPeriods, RowVersionGuard.Encode(v.RowVersion));

    public static PayPersonView Person(PayRuleVersion v) =>
        v.EmployeeId is { } employeeId
            ? new PayPersonView("Employee", employeeId, v.Employee?.FullName ?? string.Empty)
            : new PayPersonView("Worker", v.WorkerId!.Value, v.Worker?.FullName ?? string.Empty);

    public static object Snapshot(PayRuleVersion v) => new
    {
        v.EmployeeId, v.WorkerId, SalaryType = v.SalaryType.ToString(), v.Amount, v.StandardWorkingDays, v.ValidFrom, v.ValidTo,
        Components = v.Components.Select(c => new { Kind = c.Kind.ToString(), c.Name, c.Amount, Basis = c.Basis.ToString() }).ToList(),
    };
}
