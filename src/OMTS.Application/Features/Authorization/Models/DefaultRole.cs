using OMTS.Application.Features.Authorization.Models;
using static OMTS.Application.Features.Authorization.PermissionFunctions;

namespace OMTS.Application.Features.Authorization.Models;

public sealed record DefaultRole(string Name, string Description, IReadOnlyList<DefaultRolePermission> Permissions);
