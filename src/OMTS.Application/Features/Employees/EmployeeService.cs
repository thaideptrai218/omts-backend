using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// LABOR-01 – a Zone Manager adds and edits the employees of their zones, the Cooperative Owner every employee; an
/// employee is never deleted. The Owner can create the staff account in the same save (LABOR-01.5); that link never
/// grants permissions by itself.
/// </summary>
public sealed class EmployeeService(
    IEmployeeRepository employees,
    ICooperativeRepository cooperatives,
    StaffAccountService staffAccounts,
    EmployeeCallerAccess callers,
    EmployeeProfileValidator validator,
    EmployeeQueryService queries,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<EmployeeSaved>> CreateAsync(SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        var caller = await callers.GetAsync(cancellationToken);
        if (request.Account is not null && !caller.IsOwner)
        {
            return AppError.Forbidden;
        }

        if (await RefuseOwnerRightsAsync(request.Account, cancellationToken) is { } refused)
        {
            return refused;
        }

        var checkedInput = await validator.ValidateAsync(request, null, caller, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        var input = checkedInput.Value!;
        // Step 5: a Zone Manager confirms that the Owner approved in person; nothing is saved otherwise (LABOR-01.0.E3).
        if (!caller.IsOwner && !request.OwnerApproved)
        {
            return AppError.Conflict(UserMessages.OwnerApprovalNeeded(input.FullName));
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var employee = new Employee { CooperativeId = cooperative.Id };
        EmployeeProfileChanges.ApplyFields(employee, input);
        employees.Add(employee);
        EmployeeProfileChanges.SyncZones(employees, employee, input.WorkingZoneIds);
        EmployeeProfileChanges.SyncJobTypes(employees, employee, input.JobTypeIds);
        EmployeeProfileChanges.ChangeType(employees, employee, input.Type!.Value, input.TypeFrom!.Value);

        StaffAccountWithPassword? created = null;
        if (request.Account is not null)
        {
            var account = await AddAccountAsync(employee, request.Account, cancellationToken);
            if (!account.IsSuccess)
            {
                return account.Error!;
            }

            created = account.Value;
        }

        audit.Record(new AuditEntry("Employee.Created", nameof(Employee))
        {
            RecordId = employee.Id,
            After = EmployeeProfileChanges.Snapshot(employee, input.WorkingZoneIds, input.JobTypeIds),
            Reason = caller.IsOwner ? null : "The Zone Manager confirmed that the Cooperative Owner approved in person.",
        });
        if (await SaveAsync(input, null, cancellationToken, created) is { } duplicate)
        {
            return duplicate;
        }

        return await SavedAsync(employee.Id, caller, created, cancellationToken);
    }

    public async Task<Result<EmployeeSaved>> UpdateAsync(Guid id, SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var caller = await callers.GetAsync(cancellationToken);
        if (!caller.Maintains(employee))
        {
            return AppError.Forbidden;
        }

        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        if (request.Account is not null)
        {
            // A new login account for an existing employee goes through POST .../{id}/account.
            return AppError.Invalid("Account");
        }

        var checkedInput = await validator.ValidateAsync(request, employee, caller, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        // The profile and its rows are read by separate queries, so a parallel save can land between them: an old version with
        // the new rows would make this same save look like no change and answer 200. The version is read last, so it tells.
        await unitOfWork.ReloadAsync(employee, cancellationToken);
        if (await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var input = checkedInput.Value!;
        var zoneIds = EmployeeProfileChanges.ZonesAfterEdit(employee, input.WorkingZoneIds, caller);
        var before = EmployeeProfileChanges.Snapshot(employee, employee.WorkingZones.Select(w => w.ZoneId).ToList(), employee.JobTypes.Select(j => j.JobTypeId).ToList());
        var accountNumberBefore = employee.BankAccount?.AccountNumber;
        EmployeeProfileChanges.ApplyFields(employee, input);
        var zonesChanged = EmployeeProfileChanges.SyncZones(employees, employee, zoneIds);
        var jobTypesChanged = EmployeeProfileChanges.SyncJobTypes(employees, employee, input.JobTypeIds);
        var typeChanged = input is { Type: { } type, TypeFrom: { } from } && EmployeeProfileChanges.ChangeType(employees, employee, type, from);

        // These are rows of their own: the profile itself must still get a new version and "last updated by".
        if (zonesChanged || jobTypesChanged || typeChanged)
        {
            unitOfWork.MarkChanged(employee);
        }

        // LABOR-03 and LABOR-06 (L3): a changed phone number logs the employee out of the employee app (BR-LABOR-02), here.
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, EmployeeProfileChanges.Snapshot(employee, zoneIds, input.JobTypeIds, accountNumberBefore));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Employee.Updated", nameof(Employee)) { RecordId = employee.Id, Before = changedBefore, After = changedAfter });
        }

        if (await SaveAsync(input, employee.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return await SavedAsync(employee.Id, caller, null, cancellationToken);
    }

    /// <summary>
    /// Saves; a parallel save of the same phone number passes the pre-check, so the unique index refuses the second one,
    /// which then gets the same MSG-LABOR-03 as the pre-check, and a login account whose username, phone number or email was
    /// taken meanwhile gets MSG-ACCOUNT-09. On an edit, any other duplicate is a type period, working zone or job type row
    /// that a parallel save of this employee just added: its insert meets the index before the version check, so it answers
    /// what that check would, MSG06. Other duplicates are left to the exception handler.
    /// </summary>
    private async Task<AppError?> SaveAsync(EmployeeInput input, Guid? employeeId, CancellationToken cancellationToken, StaffAccountWithPassword? created = null)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await validator.PhoneRefusalAsync(input.PhoneNumber, employeeId, cancellationToken) is { } phoneTaken)
            {
                return phoneTaken;
            }

            if (created is not null && await FindTakenFieldAsync(created, cancellationToken) is { } fieldTaken)
            {
                return fieldTaken;
            }

            if (employeeId is not null)
            {
                return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
            }

            throw;
        }
    }

    private Task<AppError?> FindTakenFieldAsync(StaffAccountWithPassword created, CancellationToken cancellationToken) =>
        staffAccounts.FindTakenFieldAsync(created.Account.Username, created.Account.PhoneNumber, created.Account.Email, null, cancellationToken);

    /// <summary>Creates the login account of an employee who has none yet (Owner only); the temporary password is returned once.</summary>
    public async Task<Result<EmployeeSaved>> CreateAccountAsync(Guid id, NewEmployeeAccount request, CancellationToken cancellationToken)
    {
        if (await RefuseOwnerRightsAsync(request, cancellationToken) is { } refused)
        {
            return refused;
        }

        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        if (employee.AccountId is not null || employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.Invalid("Account");
        }

        var created = await AddAccountAsync(employee, request, cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            if (await FindTakenFieldAsync(created.Value!, cancellationToken) is { } taken)
            {
                return taken;
            }

            throw;
        }

        return await SavedAsync(employee.Id, await callers.GetAsync(cancellationToken), created.Value, cancellationToken);
    }

    /// <summary>LABOR-01.5: the display name and phone number are the employee's; no phone number is MSG-GEN-01 "Phone number".</summary>
    private async Task<Result<StaffAccountWithPassword>> AddAccountAsync(Employee employee, NewEmployeeAccount request, CancellationToken cancellationToken) =>
        // A username or phone collision is MSG-ACCOUNT-09 naming the field, as in ACCOUNT-09 (LABOR-01).
        await staffAccounts.AddStaffAccountAsync(
            new CreateStaffAccountRequest(employee.FullName, request.Username, employee.PhoneNumber ?? string.Empty, request.Email, null, request.Grants),
            employee,
            cancellationToken);

    // An attempt to give Owner rights is refused (the ACS rule) before anything is added, so only its audit row is saved.
    private async Task<AppError?> RefuseOwnerRightsAsync(NewEmployeeAccount? account, CancellationToken cancellationToken)
    {
        if (account is null || staffAccounts.RefuseOtherSubjectType(account.SubjectType, account.Username) is not { } refused)
        {
            return null;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return refused;
    }

    private async Task<EmployeeSaved> SavedAsync(Guid id, EmployeeCaller caller, StaffAccountWithPassword? created, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved("Employee profile");
        return new EmployeeSaved(
            await queries.ViewAsync((await employees.GetForEditAsync(id, cancellationToken))!, caller, cancellationToken),
            saved.Text,
            saved.Code,
            created?.TemporaryPassword,
            created?.TemporaryPasswordExpiresAt,
            created?.MessageCode);
    }
}
