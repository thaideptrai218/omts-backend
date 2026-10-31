using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record RolePermissionInput(string FunctionCode, IReadOnlyList<PermissionAction> Actions, DataScope DataScope = DataScope.AssignedZone);
