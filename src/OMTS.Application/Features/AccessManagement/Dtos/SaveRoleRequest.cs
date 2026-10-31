using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<RolePermissionInput> Permissions, string? RowVersion = null);
