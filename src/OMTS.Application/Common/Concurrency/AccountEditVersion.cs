using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Concurrency;

/// <summary>
/// The <c>rowVersion</c> that account forms (own profile, staff account) send back. Logins write their
/// counters to the account row, so the row version changes on every login or failed login and would make
/// open forms stale (MSG06 nobody caused). The time of the last real edit changes only on edits: the stamp
/// interceptor leaves it alone when only the login fields changed.
/// </summary>
public static class AccountEditVersion
{
    public static string Of(Account account) =>
        Convert.ToBase64String(BitConverter.GetBytes((account.UpdatedAt ?? account.CreatedAt).UtcTicks));
}
