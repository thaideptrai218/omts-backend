namespace OMTS.Infrastructure.Payments;

/// <summary>Account numbers that <see cref="FakePaymentNotificationService"/> treats differently, for tests and client work.</summary>
public sealed class FakePaymentOptions
{
    /// <summary>Refused by the service.</summary>
    public List<string> RejectAccounts { get; set; } = [];

    /// <summary>The service never answers: the request ends at <c>Payments:TimeoutSeconds</c>.</summary>
    public List<string> TimeoutAccounts { get; set; } = [];

    /// <summary>
    /// The secret that signs the notifications of the fake service (API-13, header <see cref="FakeBankNotificationParser.SignatureHeader"/>). Empty or blank (the default) accepts
    /// no notification. A secret: set it in user-secrets or the environment, never in a committed file.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}
