using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Workforce.Dtos;

namespace OMTS.Application.Features.Workforce;

/// <summary>
/// Pickers of LAB-03 (assignees) and LAB-04 (attendance people) for one zone: Employed employees whose working
/// zones include the zone, In Use workers and In Use teams. Only id, name, team and status: no phone or citizen ID.
/// </summary>
public sealed class LaborPickerService(IWorkforceRepository workforce, IStatusCatalog statusCatalog)
{
    public async Task<IReadOnlyList<LaborPickerItem>> ListAsync(Guid zoneId, string? search, CancellationToken cancellationToken)
    {
        var people = await workforce.ListPickableAsync(zoneId, search, cancellationToken);
        return people.Employees.Select(e => new LaborPickerItem("Employee", e.Id, e.FullName, null, statusCatalog.DisplayNameOf(e.StatusId)))
            .Concat(people.Workers.Select(w => new LaborPickerItem(
                "Worker", w.Id, w.FullName, w.Contractor is { } t ? new TeamRef(t.Id, t.Name) : null, statusCatalog.DisplayNameOf(w.StatusId))))
            .Concat(people.Teams.Select(t => new LaborPickerItem("Team", t.Id, t.Name, null, statusCatalog.DisplayNameOf(t.StatusId))))
            .ToList();
    }
}
