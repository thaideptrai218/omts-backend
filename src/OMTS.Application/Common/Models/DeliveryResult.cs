namespace OMTS.Application.Common.Models;

/// <summary>
/// Outcome of a send through an external service (email, SMS, push). Provider errors and timeouts are
/// returned as a failed result, never thrown: callers map them to MSG-ACCOUNT-13 (or MSG-ACCOUNT-20 in password recovery)
/// and leave business data unchanged.
/// </summary>
public sealed record DeliveryResult(bool Succeeded, string? MessageId, string? ErrorCode)
{
    public static DeliveryResult Sent(string? messageId) => new(true, messageId, null);

    public static DeliveryResult Failed(string errorCode) => new(false, null, errorCode);
}
