using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.Features.Authorization.Models;

/// <summary>Permissions of a default role on one function; null actions = every supported action.</summary>
public sealed record DefaultRolePermission(string FunctionCode, IReadOnlyList<PermissionAction>? Actions = null);
