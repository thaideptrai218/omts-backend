using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees;

/// <summary>All three fields or none (LAB-01).</summary>
public sealed record EmployeeBankAccountInput(string? BankName, string? AccountNumber, string? AccountHolderName);

/// <summary>
/// Optional login account created together with the employee (ACS-09 extends LAB-01). Display name and
/// phone default to the employee's; at least one zone–role row.
/// </summary>
public sealed record NewEmployeeAccount(
    string Username,
    string? DisplayName,
    string? PhoneNumber,
    string? Email,
    IReadOnlyList<GrantDraft> Grants,
    SubjectType? SubjectType = null);

/// <param name="AccountId">Link an existing Staff/Owner account (not with <paramref name="Account"/>).</param>
/// <param name="Account">Create a new staff account in the same save (create only).</param>
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
    Guid? AccountId,
    NewEmployeeAccount? Account = null,
    string? RowVersion = null);

/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06 (optional for older clients).</param>
public sealed record EndEmploymentRequest(DateOnly? EndDate, string? RowVersion = null);

/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06 (optional for older clients).</param>
public sealed record ReinstateEmployeeRequest(string? RowVersion = null);

public sealed record ZoneRef(Guid Id, string Code, string Name);

public sealed record LinkedAccountView(Guid Id, string Username, string DisplayName, string Status);

public sealed record EmployeeBankAccountView(string BankName, string AccountNumber, string AccountHolderName);

public sealed record EmployeeView(
    Guid Id,
    string FullName,
    string? PhoneNumber,
    DateOnly? Birthday,
    string? Address,
    string? Area,
    string? Ward,
    string? OtherInformation,
    string? Notes,
    IReadOnlyList<ZoneRef> WorkingZones,
    EmployeeBankAccountView? BankAccount,
    LinkedAccountView? Account,
    string Status,
    DateOnly? EmploymentEndDate,
    string RowVersion);

public sealed record EmployeeListItem(Guid Id, string FullName, string? PhoneNumber, IReadOnlyList<ZoneRef> WorkingZones, LinkedAccountView? Account, string Status);

/// <param name="TemporaryPassword">Only when a login account was created in the same save; shown once.</param>
public sealed record EmployeeSaved(EmployeeView Employee, string Message, string? TemporaryPassword = null, DateTimeOffset? TemporaryPasswordExpiresAt = null);

/// <summary>Asks the web to let the Owner review the linked account (lock it, or unlock / re-grant it).</summary>
public sealed record FollowUp(string Action, Guid AccountId);

public sealed record EmploymentChanged(EmployeeView Employee, FollowUp? FollowUp);

public sealed record LinkableAccount(Guid Id, string DisplayName, string Username, string? PhoneNumber, string Status);
