using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// The photo of an employee (the "Thêm ảnh" circle of the KiotViet form): JPEG or PNG, at most 2 MB, one per profile. Whoever maintains
/// the employee sets or removes it; the old file is deleted once the new key is saved.
/// </summary>
public sealed class EmployeePhotoService(
    IEmployeeRepository employees,
    EmployeeCallerAccess callers,
    EmployeeQueryService queries,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string PhotoCategory = "employee-photos";

    public async Task<Result<EmployeeView>> SetAsync(Guid employeeId, IncomingFile? file, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(employeeId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var validated = FileValidator.Validate(file is null ? [] : [file], FileRules.EmployeePhoto);
        if (file is null || !validated.IsValid)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Photo", validated.Problem);
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], PhotoCategory, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.UploadFailed with { LegacyCode = MessageCodes.Legacy.Msg04 });
        }

        return await ReplaceAsync(loaded.Value, uploaded.Key, cancellationToken);
    }

    public async Task<Result<EmployeeView>> RemoveAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(employeeId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var (employee, caller) = loaded.Value;
        return employee.PhotoUrl is null ? await queries.ViewAsync(employee, caller, cancellationToken) : await ReplaceAsync(loaded.Value, null, cancellationToken);
    }

    /// <summary>The employee the caller sees (404) and maintains (403), while Employed (409 otherwise).</summary>
    private async Task<Result<(Employee Employee, EmployeeCaller Caller)>> LoadAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var caller = await callers.GetAsync(cancellationToken);
        if (await employees.GetForEditAsync(employeeId, cancellationToken) is not { } employee || !caller.Sees(employee))
        {
            return AppError.NotFound;
        }

        if (!caller.Maintains(employee))
        {
            return AppError.Forbidden;
        }

        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        return (employee, caller);
    }

    private async Task<Result<EmployeeView>> ReplaceAsync((Employee Employee, EmployeeCaller Caller) loaded, string? newKey, CancellationToken cancellationToken)
    {
        var (employee, caller) = loaded;
        var old = employee.PhotoUrl;
        audit.Record(new AuditEntry("Employee.Updated", nameof(Employee))
        {
            RecordId = employee.Id,
            RecordCode = employee.Code,
            Before = new { HasPhoto = old is not null },
            After = new { HasPhoto = newKey is not null },
        });
        employee.PhotoUrl = newKey;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (old is not null)
        {
            await storage.DeleteAsync(old, cancellationToken);
        }

        return await queries.ViewAsync(employee, caller, cancellationToken);
    }
}
