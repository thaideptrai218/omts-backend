namespace OMTS.Application.Common.Models;

/// <summary>Answer of the Zalo Open API: the value (user id or phone number) when <see cref="Outcome"/> is Found.</summary>
public sealed record ZaloLookup(ZaloLookupOutcome Outcome, string? Value)
{
    public static ZaloLookup Found(string value) => new(ZaloLookupOutcome.Found, value);

    public static readonly ZaloLookup Rejected = new(ZaloLookupOutcome.Rejected, null);

    public static readonly ZaloLookup Unavailable = new(ZaloLookupOutcome.Unavailable, null);
}
