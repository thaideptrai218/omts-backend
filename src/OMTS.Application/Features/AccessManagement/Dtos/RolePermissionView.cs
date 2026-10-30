using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record RolePermissionView(string FunctionCode, IReadOnlyList<string> Actions, DataScope DataScope);
