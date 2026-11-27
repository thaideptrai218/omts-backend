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

/// <summary>Links an existing staff account that has no employee yet.</summary>
public sealed record LinkEmployeeAccountRequest(Guid? AccountId);

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
/// <param name="LockAccount">Also lock the linked staff account (Owner only), after the client asked the Owner to confirm.</param>
public sealed record EndEmploymentRequest(DateOnly? LastWorkingDay, string? RowVersion, bool LockAccount = false);

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

/// <param name="Account">The linked account's basic identity and status, within the caller's employee scope. Managing accounts remains Owner-only.</param>
/// <param name="MonthHours">Paid hours worked from the first of this month to today (break deducted, as the payroll counts them); only for a caller with PAYROLL View (Owner, Accountant), else null.</param>
/// <param name="OutstandingWage">What the employee is still owed on Finalized payroll sheets, VND; same visibility as <paramref name="MonthHours"/>.</param>
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
    string RowVersion,
    decimal? MonthHours = null,
    decimal? OutstandingWage = null);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
/// <param name="TemporaryPassword">Only when a login account was created in the same save; shown once.</param>
/// <param name="AccountMessageCode">MSG-ACCOUNT-23 with <paramref name="TemporaryPassword"/>: the client fills it in, as for a staff account.</param>
/// <param name="FixedPassword">True when <paramref name="TemporaryPassword"/> is the fixed initial password of a seasonal worker account (the same for all of them, never expires, still changed at the first login).</param>
/// <param name="AccountNote">Why a seasonal worker was saved without the login account every one of them has: the message code of the refusal (MSG-ACCOUNT-09: the phone number or username is another account); null otherwise.</param>
/// <param name="DuplicateIdNumber">Another employee holds the same ID number (CCCD): the profile is saved all the same and the client shows this warning (L16). Null when there is none.</param>
public sealed record EmployeeSaved(
    EmployeeView Employee, string Message, string MessageCode, string? TemporaryPassword = null, DateTimeOffset? TemporaryPasswordExpiresAt = null, string? AccountMessageCode = null,
    EmployeeIdNumberWarning? DuplicateIdNumber = null, bool FixedPassword = false, string? AccountNote = null);

/// <summary>The other employee with the same ID number; <c>Message</c> is the Vietnamese warning.</summary>
public sealed record EmployeeIdNumberWarning(Guid EmployeeId, string? Code, string FullName, string Message);

/// <param name="Summary">What ending the employment changed or left open (the same as the preview); null when the employee was resumed.</param>
/// <param name="AccountLocked">The linked staff account was locked in the same request (<c>lockAccount</c> was true).</param>
/// <param name="AccountNote">Why the account was not locked although asked: OwnerOnly, NoActiveAccount, or the message code of the refusal; null otherwise.</param>
public sealed record EmploymentChanged(EmployeeView Employee, EndingConsequences? Summary = null, bool AccountLocked = false, string? AccountNote = null);

/// <summary>What ending an employment on a last working day does and leaves open (L9, L10); read-only until the Owner confirms.</summary>
/// <param name="FutureAssignments">Assignments after the last day: ending removes them.</param>
/// <param name="CheckedInNotOut">Days with a check-in and no check-out: the pay of these days is incomplete until corrected.</param>
/// <param name="AdvanceBalance">Advances paid and not yet deducted from any payslip, VND.</param>
/// <param name="UnpaidPayslips">Finalized payslips still owed, and <paramref name="UnpaidAmount"/> what is owed on them.</param>
/// <param name="Account">The linked staff account, for the Cooperative Owner only.</param>
/// <param name="AccountCanBeLocked">The caller is the Owner and the linked account is an Active staff account.</param>
public sealed record EndingConsequences(
    DateOnly LastWorkingDay,
    IReadOnlyList<OMTS.Application.Common.Interfaces.Persistence.AssignmentOnDay> FutureAssignments,
    IReadOnlyList<DateOnly> CheckedInNotOut,
    decimal AdvanceBalance,
    int UnpaidPayslips,
    decimal UnpaidAmount,
    LinkedAccountView? Account,
    bool AccountCanBeLocked);

/// <summary>The employee list query. Invalid values are refused with MSG-GEN-01 naming the parameter.</summary>
/// <param name="Search">Name, code, phone number or linked account username (contains).</param>
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
    int PageSize = 20,
    Guid[]? ZoneIds = null);

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
    IReadOnlyList<Guid>? Ids = null,
    Guid[]? ZoneIds = null);

/// <param name="Row">The row number in the file.</param>
/// <param name="Fields">The invalid fields, named as in the API (e.g. "Full name", "Working zones").</param>
/// <param name="Message">The message of the refusal.</param>
/// <param name="MessageCode">MSG-GEN-01, MSG-LABOR-03, … or null for a text that has no code yet.</param>
public sealed record EmployeeImportError(int Row, IReadOnlyList<string> Fields, string Message, string? MessageCode = null);

/// <summary>The rows without an error are imported; <paramref name="Errors"/> lists the rows that were skipped.</summary>
public sealed record EmployeeImportResult(int Imported, IReadOnlyList<EmployeeImportError> Errors);
