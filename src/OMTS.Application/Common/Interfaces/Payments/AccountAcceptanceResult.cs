namespace OMTS.Application.Common.Interfaces.Payments;

/// <param name="Reason">A short technical code for the log. Never shown to the user and never holding the account number.</param>
public sealed record AccountAcceptanceResult(AccountAcceptanceOutcome Outcome, string? Reason = null)
{
    public static readonly AccountAcceptanceResult Accepted = new(AccountAcceptanceOutcome.Accepted);

    public static AccountAcceptanceResult Rejected(string reason) => new(AccountAcceptanceOutcome.Rejected, reason);

    public static AccountAcceptanceResult Unavailable(string reason) => new(AccountAcceptanceOutcome.Unavailable, reason);
}
