using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record RoleView(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    IReadOnlyList<RolePermissionView> Permissions,
    string RowVersion);
