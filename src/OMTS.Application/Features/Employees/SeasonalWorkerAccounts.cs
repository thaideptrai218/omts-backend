namespace OMTS.Application.Features.Employees;

/// <summary>
/// The login accounts of seasonal workers (owner decision of 25/11/2026): each seasonal worker has one, with the fixed initial password
/// <see cref="InitialPassword"/>, which must be changed at the first login; a reset gives it back. Staff accounts keep random one-time
/// temporary passwords. The fixed password meets <c>PasswordPolicy</c> (9 characters, letters and digits).
/// </summary>
public static class SeasonalWorkerAccounts
{
    /// <summary>The one initial password of every seasonal account; the only place it is written.</summary>
    public const string InitialPassword = "OMTS@1234";

    /// <summary>
    /// "Issued" far in the future so that the 24-hour expiry of a temporary password never applies: a seasonal worker is called when there is
    /// work and may sign in for the first time days later (the same value the demo accounts use).
    /// </summary>
    public static readonly DateTimeOffset NeverExpiresIssuedAt = new(2999, 12, 31, 0, 0, 0, TimeSpan.Zero);
}
