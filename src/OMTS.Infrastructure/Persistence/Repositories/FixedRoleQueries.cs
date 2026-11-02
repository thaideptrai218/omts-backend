using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// The one place where queries keep to the fixed roles (BR-FARM-07). A role row with another code, an Owner-made role
/// kept by the FixedRoles migration with a placeholder code, is not a role: it is not listed, and its pairs give no
/// permission, no login and no zone visibility.
/// </summary>
internal static class FixedRoleQueries
{
    /// <summary>The rows of the fixed roles.</summary>
    public static IQueryable<Role> OfFixedRoles(this IQueryable<Role> roles) =>
        roles.Where(r => FixedRoles.Codes.Contains(r.Code));

    /// <summary>Effective pairs whose role is a fixed role: the pairs that count for permissions, login and recipients.</summary>
    public static IQueryable<AccessGrant> EffectiveOfFixedRoles(this IQueryable<AccessGrant> grants) =>
        grants.Where(g => g.StatusId == StatusIds.AccessGrant.Effective && FixedRoles.Codes.Contains(g.Role.Code));
}
