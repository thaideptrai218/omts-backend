using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

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
