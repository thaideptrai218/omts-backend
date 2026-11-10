using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>All three fields or none (LAB-01).</summary>
public sealed record EmployeeBankAccountInput(string? BankName, string? AccountNumber, string? AccountHolderName);

/// <summary>
/// The staff account created together with the employee (LABOR-01.5, Cooperative Owner only): the display name and the
/// phone number are those of the employee. The zone–role rows are optional, as the pairs can be granted later in FARM-05.
/// </summary>
public sealed record NewEmployeeAccount(
    string Username,
    string? Email,
    IReadOnlyList<GrantDraft>? Grants = null,
    SubjectType? SubjectType = null);

/// <param name="PhoneNumber">Optional; empty is no phone number.</param>
/// <param name="EmployeeType">
/// Required when adding. When editing, a type other than the one in force on <paramref name="EmployeeTypeFrom"/> starts a
/// new period from that date; left out, the type stays.
/// </param>
/// <param name="EmployeeTypeFrom">The date the type applies from: today by default when adding, required with a new type when editing.</param>
/// <param name="JobTypeIds">Optional; what is sent replaces the job types of the profile.</param>
/// <param name="WorkingZoneIds">At least one. A Zone Manager chooses among their own zones; a zone of the profile outside them stays either way.</param>
/// <param name="OwnerApproved">
/// A Zone Manager adding an employee confirms that the Cooperative Owner approved in person; refused with MSG-LABOR-01
/// otherwise. Not needed from the Owner, not used on edit.
/// </param>
/// <param name="Account">Create a staff account in the same save (add only, Cooperative Owner only).</param>
/// <param name="Code">
/// Optional: empty gives NV000001… on add and leaves the code on edit; a typed code is unique in the cooperative, at most 20
/// characters of letters, digits and "-" (stored in upper case).
/// </param>
/// <param name="Gender">Optional. Left out (null) on an edit, it stays; it cannot be cleared once set.</param>
/// <param name="IdNumber">Optional, 9 or 12 digits. Null on an edit leaves it, an empty text clears it. Never in a file.</param>
/// <param name="StartDate">Optional first day of work, not after today. Left out (null) on an edit, it stays; it cannot be cleared once set.</param>
public sealed record SaveEmployeeRequest(
    string? FullName,
    string? PhoneNumber,
    DateOnly? Birthday,
    string? Address,
    string? Area,
    string? Ward,
    string? OtherInformation,
    string? Notes,
    IReadOnlyList<Guid>? WorkingZoneIds,
    EmployeeBankAccountInput? BankAccount,
    EmployeeType? EmployeeType = null,
    DateOnly? EmployeeTypeFrom = null,
    IReadOnlyList<Guid>? JobTypeIds = null,
    bool OwnerApproved = false,
    NewEmployeeAccount? Account = null,
    string? RowVersion = null,
    string? Code = null,
    Gender? Gender = null,
    string? IdNumber = null,
    DateOnly? StartDate = null);

/// <param name="ZoneId">One of the caller's zones.</param>
public sealed record AddWorkingZoneRequest(Guid? ZoneId);

/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06.</param>
public sealed record EndEmploymentRequest(DateOnly? LastWorkingDay, string? RowVersion);

/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06.</param>
/// <param name="PhoneNumber">Left out or null keeps the phone number of the profile; another number replaces it; empty clears it (LABOR-01.4.E1).</param>
public sealed record ResumeEmployeeRequest(string? RowVersion, string? PhoneNumber = null);

public sealed record ZoneRef(Guid Id, string Code, string Name);

public sealed record JobTypeRef(Guid Id, string Name, string Status);

public sealed record EmployeeTypePeriodView(EmployeeType Type, DateOnly EffectiveFrom);

public sealed record LinkedAccountView(Guid Id, string Username, string DisplayName, string Status);

public sealed record EmployeeBankAccountView(string BankName, string AccountNumber, string AccountHolderName);

/// <param name="CanMaintain">The caller may edit, end or resume the employee: Owner, All zones, or a shared zone.</param>
/// <param name="HasStaffAccount">A staff account has the phone number of the employee (BR-LABOR-01).</param>
/// <param name="Code">Null only for a profile older than the code; every profile has one after the migration.</param>
/// <param name="Birthday">This and the other personal fields (gender, ID number, start date, address…), down to <paramref name="BankAccount"/>, only when <paramref name="CanMaintain"/>.</param>
/// <param name="Account">The linked staff account; for the Cooperative Owner only.</param>
public sealed record EmployeeView(
    Guid Id,
    string? Code,
    string FullName,
    string? PhoneNumber,
    EmployeeType? EmployeeType,
    IReadOnlyList<EmployeeTypePeriodView> TypeHistory,
    IReadOnlyList<JobTypeRef> JobTypes,
    IReadOnlyList<ZoneRef> WorkingZones,
    string Status,
    DateOnly? LastWorkingDay,
    bool CanMaintain,
    bool HasStaffAccount,
    DateOnly? Birthday,
    Gender? Gender,
    string? IdNumber,
    DateOnly? StartDate,
    string? Address,
    string? Area,
    string? Ward,
    string? OtherInformation,
    string? Notes,
    EmployeeBankAccountView? BankAccount,
    LinkedAccountView? Account,
    string RowVersion);

/// <param name="Account">The linked staff account; for the Cooperative Owner only.</param>
public sealed record EmployeeListItem(
    Guid Id,
    string? Code,
    string FullName,
    string? PhoneNumber,
    EmployeeType? EmployeeType,
    IReadOnlyList<JobTypeRef> JobTypes,
    IReadOnlyList<ZoneRef> WorkingZones,
    string Status,
    DateOnly? LastWorkingDay,
    bool CanMaintain,
    LinkedAccountView? Account,
    string RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
/// <param name="TemporaryPassword">Only when a login account was created in the same save; shown once.</param>
/// <param name="AccountMessageCode">MSG-ACCOUNT-23 with <paramref name="TemporaryPassword"/>: the client fills it in, as for a staff account.</param>
public sealed record EmployeeSaved(
    EmployeeView Employee, string Message, string MessageCode, string? TemporaryPassword = null, DateTimeOffset? TemporaryPasswordExpiresAt = null, string? AccountMessageCode = null);

public sealed record EmploymentChanged(EmployeeView Employee);

/// <summary>The employee list query. Invalid values are refused with MSG-GEN-01 naming the parameter.</summary>
/// <param name="Search">Name, code or phone number (contains).</param>
/// <param name="StatusId">Employed or Left.</param>
/// <param name="ZoneId">A working zone of the employee.</param>
/// <param name="EmployeeType">The type in force today: SeasonalWorker or Staff.</param>
/// <param name="JobTypeId">A job type on the profile.</param>
/// <param name="SortBy">name, code, phone, type, status or lastWorkingDay; without it, the employees of the caller's zones first, then by name.</param>
/// <param name="SortDir">asc (default) or desc.</param>
public sealed record EmployeeListQuery(
    string? Search = null,
    int? StatusId = null,
    Guid? ZoneId = null,
    EmployeeType? EmployeeType = null,
    Guid? JobTypeId = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>The export query: the list query without paging, or only the employees of <paramref name="Ids"/>, whose other filters are ignored.</summary>
/// <param name="Ids">At most 200 ids: the rows selected on the screen.</param>
public sealed record EmployeeExportQuery(
    string? Search = null,
    int? StatusId = null,
    Guid? ZoneId = null,
    EmployeeType? EmployeeType = null,
    Guid? JobTypeId = null,
    string? SortBy = null,
    string? SortDir = null,
    IReadOnlyList<Guid>? Ids = null);

/// <param name="Row">The row number in the file.</param>
/// <param name="Fields">The invalid fields, named as in the API (e.g. "Full name", "Working zones").</param>
/// <param name="Message">The message of the refusal.</param>
/// <param name="MessageCode">MSG-GEN-01, MSG-LABOR-03, … or null for a text that has no code yet.</param>
public sealed record EmployeeImportError(int Row, IReadOnlyList<string> Fields, string Message, string? MessageCode = null);

/// <summary>The rows without an error are imported; <paramref name="Errors"/> lists the rows that were skipped.</summary>
public sealed record EmployeeImportResult(int Imported, IReadOnlyList<EmployeeImportError> Errors);
