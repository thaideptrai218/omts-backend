using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Who is working on employee profiles (BR-LABOR-01): every Zone Manager sees every employee and maintains those who
/// have a working zone among the zones of their pairs. The Owner and an All zones pair maintain everyone.
/// </summary>
public sealed record EmployeeCaller(bool IsOwner, PermittedZones Zones)
{
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
            access?.PermittedZonesFor(PermissionFunctions.LaborWorkers, PermissionAction.Edit) ?? PermittedZones.None);
    }
}
