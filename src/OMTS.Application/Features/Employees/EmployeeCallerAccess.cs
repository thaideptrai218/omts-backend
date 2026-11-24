using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Who is working on employee profiles (BR-LABOR-01, decision of 24/11/2026): a caller whose access is limited to some zones sees, and maintains, only the employees who have a working zone among
/// those zones. The Owner and a caller granted all zones see and maintain everyone.
/// </summary>
/// <param name="Zones">The zones of the pairs that may edit employees (maintenance).</param>
/// <param name="ViewZones">The zones of the pairs that may view employees (what the caller sees); null: the same as <paramref name="Zones"/>.</param>
public sealed record EmployeeCaller(bool IsOwner, PermittedZones Zones, PermittedZones? ViewZones = null)
{
    /// <summary>The zones the caller sees employees of.</summary>
    public PermittedZones Visible => IsOwner ? PermittedZones.Everywhere : ViewZones ?? Zones;

    /// <summary>The zones to filter a query by: null when the caller sees every employee, else the zones (an empty list sees nobody).</summary>
    public IReadOnlyCollection<Guid>? VisibleZoneIds => Visible.AllZones ? null : Visible.ZoneIds.ToList();

    /// <summary>The caller sees the employee: some working zone of the employee is a zone the caller sees.</summary>
    public bool Sees(Employee employee) => Visible.AllZones || employee.WorkingZones.Any(w => Visible.ZoneIds.Contains(w.ZoneId));

    public bool HasZone(Guid zoneId) => Zones.AllZones || Zones.ZoneIds.Contains(zoneId);

    public bool Maintains(Employee employee) => Zones.AllZones || employee.WorkingZones.Any(w => Zones.ZoneIds.Contains(w.ZoneId));
}

public sealed class EmployeeCallerAccess(ICurrentUser currentUser, PermissionService permissions)
{
    public async Task<EmployeeCaller> GetAsync(CancellationToken cancellationToken)
    {
        var access = currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;
        return new EmployeeCaller(
            access?.IsCooperativeOwner == true,
            access?.PermittedZonesFor(PermissionFunctions.LaborWorkers, PermissionAction.Edit) ?? PermittedZones.None,
            // The pairs' own zones: PermittedZonesFor would answer "every zone" for a function of the whole cooperative.
            access?.PairZonesFor(PermissionFunctions.LaborWorkers, PermissionAction.View) ?? PermittedZones.None);
    }
}
