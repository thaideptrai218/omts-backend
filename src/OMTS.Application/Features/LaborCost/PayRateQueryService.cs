using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce.Dtos;

namespace OMTS.Application.Features.LaborCost;

/// <summary>LAB-06 reads: the people who can get a rate (with today's rate) and the version history of one person.</summary>
public sealed class PayRateQueryService(IPayRateRepository rates, PayRateViews views, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<PayPersonListItem>> PeopleAsync(string? search, CancellationToken cancellationToken)
    {
        var (employees, workers) = await rates.ListPayablePeopleAsync(search, cancellationToken);
        var today = timeProvider.Today();
        var current = await rates.ListOverlappingAsync(today, today, cancellationToken);
        PayRateSummary? Current(Guid? employeeId, Guid? workerId) =>
            current.FirstOrDefault(v => (employeeId != null && v.EmployeeId == employeeId) || (workerId != null && v.WorkerId == workerId)) is { } v
                ? new PayRateSummary(v.Id, v.SalaryType, v.Amount, v.ValidFrom, v.ValidTo)
                : null;

        return employees.Select(e => new PayPersonListItem("Employee", e.Id, e.FullName, null, Current(e.Id, null)))
            .Concat(workers.Select(w => new PayPersonListItem(
                "Worker", w.Id, w.FullName, w.Contractor is { } t ? new TeamRef(t.Id, t.Name) : null, Current(null, w.Id))))
            .ToList();
    }

    /// <summary>Every version of one person (exactly one of the ids), newest first.</summary>
    public async Task<Result<IReadOnlyList<PayRateView>>> HistoryAsync(Guid? employeeId, Guid? workerId, CancellationToken cancellationToken)
    {
        if ((employeeId is null) == (workerId is null))
        {
            return AppError.Invalid("Person");
        }

        return Result<IReadOnlyList<PayRateView>>.Success(
            await views.ViewsAsync(await rates.ListForPersonAsync(employeeId, workerId, cancellationToken), cancellationToken));
    }

    public async Task<Result<PayRateView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await rates.GetAsync(id, cancellationToken) is { } version ? await views.ViewAsync(version, cancellationToken) : AppError.NotFound;
}
