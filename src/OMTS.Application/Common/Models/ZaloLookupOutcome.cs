namespace OMTS.Application.Common.Models;

public enum ZaloLookupOutcome
{
    Found,

    /// <summary>Zalo refused the token: wrong, expired, already used or issued for another Zalo App.</summary>
    Rejected,

    /// <summary>Zalo did not answer, or the server is not configured to ask it (MSG04).</summary>
    Unavailable,
}
