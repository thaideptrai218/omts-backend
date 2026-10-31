using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

/// <param name="TemporaryPassword">Only when a login account was created in the same save; shown once.</param>
public sealed record EmployeeSaved(EmployeeView Employee, string Message, string MessageCode, string? TemporaryPassword = null, DateTimeOffset? TemporaryPasswordExpiresAt = null);
