using OMTS.Application.Features.Employees.Dtos;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common;
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
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// LAB-01 – the Owner keeps one profile per permanent employee and ends employment instead of deleting.
/// The login account can be created in the same save (ACS-09 extends LAB-01); linking an account never
/// grants permissions by itself.
/// </summary>
public sealed class EmployeeService(
    IEmployeeRepository employees,
    IAccountRepository accounts,
    ICooperativeRepository cooperatives,
    IFarmingZoneRepository zones,
    StaffAccountService staffAccounts,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Paged<EmployeeListItem>> SearchAsync(
        string? search, int? statusId, Guid? zoneId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        var (items, total) = await employees.SearchAsync(search, statusId, zoneId, page, pageSize, cancellationToken);
        return new Paged<EmployeeListItem>(
            items.Select(e => new EmployeeListItem(e.Id, e.FullName, e.PhoneNumber, Zones(e), Linked(e.Account), statusCatalog.DisplayNameOf(e.StatusId))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<EmployeeView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await employees.GetForEditAsync(id, cancellationToken) is { } employee ? View(employee) : AppError.NotFound;

    public async Task<IReadOnlyList<LinkableAccount>> LinkableAccountsAsync(CancellationToken cancellationToken) =>
        (await accounts.ListLinkableStaffAsync(cancellationToken))
            .Select(a => new LinkableAccount(a.Id, a.DisplayName, a.Username, a.PhoneNumber, statusCatalog.DisplayNameOf(a.StatusId)))
            .ToList();

    public async Task<Result<EmployeeSaved>> CreateAsync(SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        if (await RefuseOwnerRightsAsync(request.Account, cancellationToken) is { } refused)
        {
            return refused;
        }

        var checkedInput = await ValidateAsync(request, null, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        var input = checkedInput.Value!;
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var employee = new Employee { CooperativeId = cooperative.Id };
        Apply(employee, input);
        employees.Add(employee);
        foreach (var zoneId in input.WorkingZoneIds)
        {
            employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = zoneId });
        }

        if (await LinkAsync(employee, request.AccountId, cancellationToken) is { } linkError)
        {
            return linkError;
        }

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

        audit.Record(new AuditEntry("Employee.Created", nameof(Employee)) { RecordId = employee.Id, After = Snapshot(employee, input.WorkingZoneIds) });
        if (await SaveAsync(input, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return await SavedAsync(employee.Id, created, cancellationToken);
    }

    public async Task<Result<EmployeeSaved>> UpdateAsync(Guid id, SaveEmployeeRequest request, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        if (request.Account is not null)
        {
            // A new login account for an existing employee goes through POST .../{id}/account.
            return AppError.Invalid("Account");
        }

        var checkedInput = await ValidateAsync(request, employee.Id, cancellationToken);
        if (!checkedInput.IsSuccess)
        {
            return checkedInput.Error!;
        }

        if (await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var input = checkedInput.Value!;
        var before = Snapshot(employee, employee.WorkingZones.Select(w => w.ZoneId).ToList());
        Apply(employee, input);
        var removedZones = employee.WorkingZones.Where(w => !input.WorkingZoneIds.Contains(w.ZoneId)).ToList();
        var addedZones = input.WorkingZoneIds.Where(z => employee.WorkingZones.All(w => w.ZoneId != z)).ToList();
        foreach (var removed in removedZones)
        {
            employees.RemoveWorkingZone(removed);
        }

        foreach (var zoneId in addedZones)
        {
            employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = zoneId });
        }

        // Working zones are rows of their own: the profile itself must still get a new version and "last updated by".
        if (removedZones.Count > 0 || addedZones.Count > 0)
        {
            unitOfWork.MarkChanged(employee);
        }

        // A missing accountId keeps the link, so a form without the account field never unlinks; DELETE .../{id}/account does.
        if (request.AccountId is { } accountId && employee.AccountId != accountId && await LinkAsync(employee, accountId, cancellationToken) is { } linkError)
        {
            return linkError;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(employee, input.WorkingZoneIds));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Employee.Updated", nameof(Employee)) { RecordId = employee.Id, Before = changedBefore, After = changedAfter });
        }

        if (await SaveAsync(input, employee.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return await SavedAsync(employee.Id, null, cancellationToken);
    }

    /// <summary>
    /// Saves; a parallel save of the same person passes the pre-check, so the unique index refuses the second
    /// one, which then gets the same MSG-LABOR-03 as the pre-check. Other duplicates are left to the exception handler.
    /// </summary>
    private async Task<AppError?> SaveAsync(EmployeeInput input, Guid? employeeId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await employees.FindSamePersonAsync(input.FullName, input.PhoneNumber, employeeId, cancellationToken) is { } same)
            {
                return SamePerson(same);
            }

            throw;
        }
    }

    /// <summary>Creates the login account of an employee who has none yet; the temporary password is returned once.</summary>
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

        if (employee.AccountId is not null)
        {
            return AppError.Invalid("Account");
        }

        var created = await AddAccountAsync(employee, request, cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(employee.Id, created.Value, cancellationToken);
    }

    /// <summary>
    /// Removes the link to the login account, audited like a link change on edit; the account and its grants stay as
    /// they are. Without a link nothing changes and no audit row is written, so a repeated call answers the same.
    /// A given <paramref name="rowVersion"/> that is not base64 answers MSG-GEN-01 in every case; one older than the
    /// profile answers MSG06 when there is a link to remove.
    /// </summary>
    public async Task<Result<EmployeeSaved>> UnlinkAccountAsync(Guid id, string? rowVersion, CancellationToken cancellationToken)
    {
        if (rowVersion is not null && !RowVersionGuard.IsWellFormed(rowVersion))
        {
            return AppError.Invalid("RowVersion");
        }

        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        if (employee.AccountId is not { } accountId)
        {
            return await SavedAsync(employee.Id, null, cancellationToken);
        }

        if (rowVersion is not null && await rowVersions.CheckAsync(employee, rowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        employee.AccountId = null;
        audit.Record(new AuditEntry("Employee.Updated", nameof(Employee))
        {
            RecordId = employee.Id, Before = new { AccountId = accountId }, After = new { AccountId = (Guid?)null },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(employee.Id, null, cancellationToken);
    }

    /// <summary>
    /// Employed → Left. Records already made stay; work or attendance after the end date blocks it (409 MSG-GEN-01
    /// "End date" with <c>count</c>, <c>attendanceCount</c> and <c>assignmentCount</c>).
    /// </summary>
    public async Task<Result<EmploymentChanged>> EndEmploymentAsync(Guid id, EndEmploymentRequest request, CancellationToken cancellationToken)
    {
        // Ending is effective at once (status Left), so the end date is today or earlier.
        if (request.EndDate is not { } endDate || endDate > timeProvider.Today())
        {
            return AppError.Invalid("End date");
        }

        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(employee, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (SameStatus(employee, StatusIds.Employee.Left) is { } same)
        {
            return same;
        }

        // LABOR-01.3.E1: the date to fix is pointed out (MSG-GEN-01 "End date"; MSG11 before), with the later records.
        var later = await employees.CountLaborRecordsAfterAsync(employee.Id, endDate, cancellationToken);
        if (later.Total > 0)
        {
            return AppError.Conflict(
                UserMessages.Invalid("End date") with { LegacyCode = MessageCodes.Legacy.Msg11 },
                new Dictionary<string, object?>
                {
                    ["field"] = "End date",
                    ["count"] = later.Total,
                    ["attendanceCount"] = later.AttendanceCount,
                    ["assignmentCount"] = later.AssignmentCount,
                });
        }

        employee.StatusId = StatusIds.Employee.Left;
        employee.EmploymentEndDate = endDate;
        audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Working, StatusIds.Employee.Left, $"End date {endDate:yyyy-MM-dd}");
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var followUp = employee.Account is { StatusId: StatusIds.Account.Active } ? new FollowUp("review-lock", employee.Account.Id) : null;
        return new EmploymentChanged(View(employee), followUp);
    }

    /// <summary>Left → Employed; the end date is cleared and the Owner is asked to review the linked account.</summary>
    public async Task<Result<EmploymentChanged>> ReinstateAsync(Guid id, string? rowVersion, CancellationToken cancellationToken)
    {
        var employee = await employees.GetForEditAsync(id, cancellationToken);
        if (employee is null)
        {
            return AppError.NotFound;
        }

        if (rowVersion is not null && await rowVersions.CheckAsync(employee, rowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (SameStatus(employee, StatusIds.Employee.Working) is { } same)
        {
            return same;
        }

        employee.StatusId = StatusIds.Employee.Working;
        employee.EmploymentEndDate = null;
        audit.RecordStatusChange(nameof(Employee), employee.Id, null, StatusIds.Employee.Left, StatusIds.Employee.Working);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var followUp = employee.AccountId is { } accountId ? new FollowUp("review-access", accountId) : null;
        return new EmploymentChanged(View(employee), followUp);
    }

    private async Task<Result<EmployeeInput>> ValidateAsync(SaveEmployeeRequest request, Guid? employeeId, CancellationToken cancellationToken)
    {
        if (request.AccountId is not null && request.Account is not null)
        {
            return AppError.Invalid("Account");
        }

        var (input, invalid) = EmployeeInput.From(request, timeProvider.Today());
        if (input is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var zoneRows = await zones.GetByIdsAsync(input.WorkingZoneIds, cancellationToken);
        if (zoneRows.Count != input.WorkingZoneIds.Count || zoneRows.Any(z => z.StatusId != StatusIds.Usage.InUse))
        {
            return AppError.Invalid("Working zones");
        }

        if (await employees.FindSamePersonAsync(input.FullName, input.PhoneNumber, employeeId, cancellationToken) is { } same)
        {
            return SamePerson(same);
        }

        return input;
    }

    /// <summary>Links the Staff/Owner account; no <paramref name="accountId"/> changes nothing.</summary>
    private async Task<AppError?> LinkAsync(Employee employee, Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return null;
        }

        var account = await accounts.GetByIdAsync(accountId.Value, cancellationToken);
        if (account is null || account.SubjectType is not (SubjectType.Staff or SubjectType.CooperativeOwner))
        {
            return AppError.Invalid("Account");
        }

        if (await employees.GetByAccountIdAsync(account.Id, cancellationToken) is { } linked && linked.Id != employee.Id)
        {
            return AppError.Conflict(UserMessages.AccountAlreadyLinked(linked.FullName), new Dictionary<string, object?> { ["existingEmployeeId"] = linked.Id });
        }

        employee.AccountId = account.Id;
        return null;
    }

    private async Task<Result<StaffAccountWithPassword>> AddAccountAsync(Employee employee, NewEmployeeAccount request, CancellationToken cancellationToken)
    {
        var created = await staffAccounts.AddStaffAccountAsync(
            new CreateStaffAccountRequest(
                request.DisplayName ?? employee.FullName, request.Username, request.PhoneNumber ?? employee.PhoneNumber ?? string.Empty,
                request.Email, null, request.Grants),
            employee,
            cancellationToken);

        // A username or phone collision is MSG-ACCOUNT-09 naming the field, as in ACCOUNT-09 (LABOR-01).
        return created;
    }

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

    private AppError? SameStatus(Employee employee, int target)
    {
        if (employee.StatusId != target)
        {
            return null;
        }

        return AppError.Conflict(UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(target)));
    }

    /// <summary>MSG-LABOR-03: another employee profile has the phone number (MSG03 before).</summary>
    private static AppError SamePerson(Employee same) =>
        AppError.Conflict(UserMessages.PhoneBelongsToEmployee(same.FullName), new Dictionary<string, object?> { ["existingEmployeeId"] = same.Id });

    private async Task<EmployeeSaved> SavedAsync(Guid id, StaffAccountWithPassword? created, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved("Employee profile");
        return new EmployeeSaved(
            View((await employees.GetForEditAsync(id, cancellationToken))!),
            saved.Text,
            saved.Code,
            created?.TemporaryPassword,
            created?.TemporaryPasswordExpiresAt);
    }

    private static void Apply(Employee employee, EmployeeInput input)
    {
        employee.FullName = input.FullName;
        employee.PhoneNumber = input.PhoneNumber;
        employee.Birthday = input.Birthday;
        employee.Address = input.Address;
        employee.Area = input.Area;
        employee.Ward = input.Ward;
        employee.OtherInformation = input.OtherInformation;
        employee.Notes = input.Notes;
        employee.BankAccount = input.BankAccount;
    }

    private static object Snapshot(Employee e, IReadOnlyList<Guid> zoneIds) => new
    {
        e.FullName, e.PhoneNumber, e.Birthday, e.Address, e.Area, e.Ward, e.OtherInformation, e.Notes, e.AccountId,
        WorkingZoneIds = zoneIds.Order().ToList(),
        BankAccount = e.BankAccount is { } bank
            ? new { bank.BankName, AccountNumber = EmployeeInput.MaskAccountNumber(bank.AccountNumber), bank.AccountHolderName }
            : null,
    };

    private EmployeeView View(Employee e) => new(
        e.Id, e.FullName, e.PhoneNumber, e.Birthday, e.Address, e.Area, e.Ward, e.OtherInformation, e.Notes, Zones(e),
        e.BankAccount is { } bank ? new EmployeeBankAccountView(bank.BankName, bank.AccountNumber, bank.AccountHolderName) : null,
        Linked(e.Account),
        statusCatalog.DisplayNameOf(e.StatusId),
        e.EmploymentEndDate,
        RowVersionGuard.Encode(e.RowVersion));

    private static IReadOnlyList<ZoneRef> Zones(Employee e) =>
        e.WorkingZones.Where(w => w.Zone is not null).Select(w => new ZoneRef(w.ZoneId, w.Zone.Code, w.Zone.Name)).OrderBy(z => z.Code).ToList();

    private LinkedAccountView? Linked(Account? account) =>
        account is null ? null : new LinkedAccountView(account.Id, account.Username, account.DisplayName, statusCatalog.DisplayNameOf(account.StatusId));
}
