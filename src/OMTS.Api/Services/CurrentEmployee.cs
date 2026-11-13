using System.Security.Claims;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Services;

/// <summary>
/// The employee of an employee app request. Only a principal that carries the employee subject type counts, so no other kind of
/// token can be read as an employee whatever claims it holds; the scheme and its session check already ran when it got here.
/// </summary>
internal sealed class CurrentEmployee(IHttpContextAccessor httpContextAccessor) : ICurrentEmployee
{
    public Guid? EmployeeId => ReadGuid(OmtsClaimTypes.EmployeeId);
    public Guid? SessionId => ReadGuid(OmtsClaimTypes.EmployeeSessionId);
    public Guid? DeviceId => ReadGuid(OmtsClaimTypes.EmployeeDeviceId);

    private Guid? ReadGuid(string claimType)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null || user.Identity?.IsAuthenticated != true || user.FindFirstValue(OmtsClaimTypes.SubjectType) != OmtsClaimTypes.EmployeeSubjectType)
        {
            return null;
        }

        return Guid.TryParse(user.FindFirstValue(claimType), out var value) ? value : null;
    }
}
