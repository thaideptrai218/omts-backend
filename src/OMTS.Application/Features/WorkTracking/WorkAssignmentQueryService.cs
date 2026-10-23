using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>LAB-03 list and detail of the work assignments of one zone.</summary>
public sealed class WorkAssignmentQueryService(IWorkAssignmentRepository assignments, WorkAssignmentViews views, TimeProvider timeProvider)
{
    private static readonly string[] States = ["active", "cancelled", "completed"];

    /// <summary>Date range default: the current month; at most 366 days. State: active, cancelled or completed.</summary>
    public async Task<Result<Paged<WorkAssignmentView>>> SearchAsync(
        Guid zoneId, DateOnly? from, DateOnly? to, Guid? employeeId, Guid? workerId, Guid? teamId, WorkPaymentType? paymentType,
        string? state, int page, int pageSize, CancellationToken cancellationToken)
    {
        var zone = await assignments.GetZoneAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var today = timeProvider.Today();
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);
        if (end < start || end.DayNumber - start.DayNumber > 366)
        {
            return AppError.Invalid("Date range");
        }

        var normalizedState = string.IsNullOrWhiteSpace(state) ? null : state.Trim().ToLowerInvariant();
        if (normalizedState is not null && !States.Contains(normalizedState))
        {
            return AppError.Invalid("State");
        }

        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var filter = new WorkAssignmentFilter(start, end, employeeId, workerId, teamId, paymentType, normalizedState);
        var (items, total) = await assignments.SearchAsync(zoneId, filter, page, pageSize, cancellationToken);
        var rows = new List<WorkAssignmentView>();
        foreach (var item in items)
        {
            rows.Add(await views.ViewAsync(item, zone.Name, cancellationToken));
        }

        return new Paged<WorkAssignmentView>(rows, page, pageSize, total);
    }

    /// <summary>An assignment of another zone is not found (the route zone is the permission scope).</summary>
    public async Task<Result<WorkAssignmentView>> GetAsync(Guid zoneId, Guid id, CancellationToken cancellationToken) =>
        await assignments.GetAsync(id, cancellationToken) is { } a && a.ZoneId == zoneId
            ? await views.ViewAsync(a, a.Zone.Name, cancellationToken)
            : AppError.NotFound;
}
