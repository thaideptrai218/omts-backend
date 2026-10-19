using System.Security.Claims;
using OMTS.Application.Authentication;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Authentication;

internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid? AccountId => ReadGuid(OmtsClaimTypes.Subject);
    public Guid? SessionId => ReadGuid(OmtsClaimTypes.SessionId);

    private Guid? ReadGuid(string claimType) =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(claimType), out var value) ? value : null;
}
