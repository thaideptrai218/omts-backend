using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

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
