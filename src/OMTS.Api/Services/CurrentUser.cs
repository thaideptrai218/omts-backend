using System.Security.Claims;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Services;

internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor, AmbientActor ambientActor) : ICurrentUser
{
    // Outside an HTTP request (the bootstrap command) the ambient actor is the caller, if one was set.
    // During a request only the token counts, so the ambient actor can never change who a request is.
    public Guid? AccountId => httpContextAccessor.HttpContext is null ? ambientActor.AccountId : ReadGuid(OmtsClaimTypes.Subject);
    public Guid? SessionId => ReadGuid(OmtsClaimTypes.SessionId);

    private Guid? ReadGuid(string claimType) =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue(claimType), out var value) ? value : null;
}
