using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06 (optional for older clients).</param>
public sealed record ReinstateEmployeeRequest(string? RowVersion = null);
