namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>A notification as it reached the webhook: its headers (names in any case) and its raw body, before anything reads it.</summary>
public sealed record BankNotificationRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>The transfers of an authentic notification, or why its body could not be read (the provider then gets 400).</summary>
public sealed record BankNotificationParse(IReadOnlyList<BankTransferNotice> Notices, string? Error)
{
    public static BankNotificationParse Invalid(string error) => new([], error);
}

/// <summary>
/// The incoming side of the Payment Notification Service (API-13). Each provider signs and shapes its notifications its own way, so the provider's adapter both checks that
/// a notification comes from it (a signature or a key made with the secret agreed at deployment) and reads its transfers. No provider is chosen yet: <c>None</c> accepts no
/// notification, and <c>Fake</c> (Development only) reads the generic format of the README, signed with <c>Payments:Fake:WebhookSecret</c>.
/// </summary>
public interface IBankNotificationParser
{
    /// <summary>Whether the notification comes from the service; never true without a configured secret.</summary>
    bool IsAuthentic(BankNotificationRequest request);

    /// <summary>The transfers of an authentic notification, in its order.</summary>
    BankNotificationParse Parse(BankNotificationRequest request);
}
