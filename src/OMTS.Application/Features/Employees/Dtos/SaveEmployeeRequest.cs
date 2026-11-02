using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

/// <param name="AccountId">
/// Link an existing Staff/Owner account (not with <paramref name="Account"/>). On edit, missing or null keeps the linked
/// account; <c>DELETE /api/employees/{id}/account</c> unlinks it.
/// </param>
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
