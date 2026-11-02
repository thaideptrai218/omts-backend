using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record StaffAccountView(
    Guid Id,
    string DisplayName,
    string Username,
    string? PhoneNumber,
    bool PhoneVerified,
    string? Email,
    bool EmailVerified,
    string Status,
    bool MustChangePassword,
    Guid? EmployeeId,
    string RowVersion);
