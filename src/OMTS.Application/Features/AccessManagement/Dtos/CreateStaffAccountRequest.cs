using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <param name="SubjectType">Only Staff accounts can be created; any other value is refused and audited.</param>
public sealed record CreateStaffAccountRequest(
    string DisplayName,
    string Username,
    string PhoneNumber,
    string? Email,
    Guid? EmployeeId,
    IReadOnlyList<GrantDraft> Grants,
    SubjectType? SubjectType = null);
