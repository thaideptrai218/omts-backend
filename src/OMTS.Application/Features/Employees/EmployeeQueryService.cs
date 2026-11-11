using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>LABOR-01 step 1: the employees of the cooperative, those of the caller's zones first (BR-LABOR-01).</summary>
public sealed class EmployeeQueryService(
    IEmployeeRepository employees,
    IAccountRepository accounts,
    EmployeeCallerAccess callers,
    EmployeeViewBuilder views,
    TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    /// <summary>The most employees one export file holds.</summary>
    public const int MaxExportRows = 5000;

    /// <summary>The most rows selected on the screen for one export.</summary>
    public const int MaxExportIds = 200;

    /// <summary>The longest name the column holds: a longer text can match no name or number, and need not reach the database.</summary>
    private const int MaxSearchLength = 200;

    /// <summary>The page of employees; a parameter with a value that does not exist is MSG-GEN-01 naming it.</summary>
    public async Task<Result<Paged<EmployeeListItem>>> SearchAsync(EmployeeListQuery query, CancellationToken cancellationToken)
    {
        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var filter = FilterOf(query.Search, query.StatusId, query.ZoneId, query.EmployeeType, query.JobTypeId, query.SortBy, query.SortDir, null);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        if (filter.Value is null)
        {
            return new Paged<EmployeeListItem>([], page, pageSize, 0);
        }

        var caller = await callers.GetAsync(cancellationToken);
        var (items, total) = await employees.SearchAsync(filter.Value, FirstZones(caller), page, pageSize, cancellationToken);
        return new Paged<EmployeeListItem>(items.Select(e => views.ListItem(e, caller)).ToList(), page, pageSize, total);
    }

    /// <summary>
    /// The employees of an export, in the order of the list, with the caller to decide what each row may show. With
    /// <see cref="EmployeeExportQuery.Ids"/> only those employees come and the other filters are ignored. More than
    /// <see cref="MaxExportRows"/> is refused (MSG-GEN-01 "Filter"): the caller narrows the filter.
    /// </summary>
    public async Task<Result<(IReadOnlyList<Employee> Items, EmployeeCaller Caller)>> ExportRowsAsync(EmployeeExportQuery query, CancellationToken cancellationToken)
    {
        var ids = query.Ids?.Distinct().ToList();
        if (ids?.Count > MaxExportIds)
        {
            return AppError.Invalid("Ids", $"At most {MaxExportIds} employees.");
        }

        var filter = ids is { Count: > 0 }
            ? FilterOf(null, null, null, null, null, query.SortBy, query.SortDir, ids)
            : FilterOf(query.Search, query.StatusId, query.ZoneId, query.EmployeeType, query.JobTypeId, query.SortBy, query.SortDir, null);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (filter.Value is null)
        {
            return (Array.Empty<Employee>(), caller);
        }

        var rows = await employees.ListAsync(filter.Value, FirstZones(caller), MaxExportRows, cancellationToken);
        return rows.Count > MaxExportRows
            ? AppError.Invalid("Filter", $"At most {MaxExportRows} employees: narrow the filter.")
            : (rows, caller);
    }

    private static List<Guid>? FirstZones(EmployeeCaller caller) => caller.Zones.AllZones ? null : caller.Zones.ZoneIds.ToList();

    /// <summary>The filter of the query; a success with null when the search text is longer than any name, code or number, so nothing can match.</summary>
    private Result<EmployeeSearchFilter?> FilterOf(
        string? search, int? statusId, Guid? zoneId, EmployeeType? type, Guid? jobTypeId, string? sortBy, string? sortDir, IReadOnlyCollection<Guid>? ids)
    {
        if (type is { } t && !Enum.IsDefined(t))
        {
            return AppError.Invalid("Employee type");
        }

        var sort = sortBy switch
        {
            null => (EmployeeSort?)EmployeeSort.Default,
            "name" => EmployeeSort.Name,
            "code" => EmployeeSort.Code,
            "phone" => EmployeeSort.Phone,
            "type" => EmployeeSort.Type,
            "status" => EmployeeSort.Status,
            "lastWorkingDay" => EmployeeSort.LastWorkingDay,
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort by");
        }

        if (sortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        var text = search?.Trim();
        if (text?.Length > MaxSearchLength)
        {
            return Result<EmployeeSearchFilter?>.Success(null);
        }

        return Result<EmployeeSearchFilter?>.Success(new EmployeeSearchFilter(
            text, PhoneNumbers.Normalize(text), statusId, zoneId, type, jobTypeId, timeProvider.Today(), sort.Value, sortDir == "desc", ids));
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
