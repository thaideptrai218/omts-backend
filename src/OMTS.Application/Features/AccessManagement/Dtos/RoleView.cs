using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record RoleView(Guid Id, string Code, string Name, string? Description, IReadOnlyList<RolePermissionView> Permissions);
