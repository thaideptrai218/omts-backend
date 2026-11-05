using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>LABOR-01 step 1: the employees of the cooperative, those of the caller's zones first (BR-LABOR-01).</summary>
public sealed class EmployeeQueryService(
    IEmployeeRepository employees,
    IAccountRepository accounts,
    EmployeeCallerAccess callers,
    EmployeeViewBuilder views)
{
    public async Task<Paged<EmployeeListItem>> SearchAsync(
        string? search, int? statusId, Guid? zoneId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var caller = await callers.GetAsync(cancellationToken);
        var firstZones = caller.Zones.AllZones ? null : caller.Zones.ZoneIds.ToList();
        var (items, total) = await employees.SearchAsync(search, statusId, zoneId, firstZones, page, pageSize, cancellationToken);
        return new Paged<EmployeeListItem>(items.Select(e => views.ListItem(e, caller)).ToList(), page, pageSize, total);
    }

    public async Task<Result<EmployeeView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await employees.GetForEditAsync(id, cancellationToken) is not { } employee)
        {
            return AppError.NotFound;
        }

        return await ViewAsync(employee, await callers.GetAsync(cancellationToken), cancellationToken);
    }

    /// <summary>The view of a saved or read employee: whether a staff account has its phone number is asked here only.</summary>
    public async Task<EmployeeView> ViewAsync(Employee employee, EmployeeCaller caller, CancellationToken cancellationToken)
    {
        var hasStaffAccount = employee.PhoneNumber is { } phone
            && await accounts.FindByPhoneAsync(phone, cancellationToken) is { SubjectType: SubjectType.Staff };
        return views.View(employee, caller, hasStaffAccount);
    }
}
