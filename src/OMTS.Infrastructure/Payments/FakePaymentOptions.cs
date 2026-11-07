namespace OMTS.Infrastructure.Payments;

/// <summary>Account numbers that <see cref="FakePaymentNotificationService"/> treats differently, for tests and client work.</summary>
public sealed class FakePaymentOptions
{
    /// <summary>Refused by the service.</summary>
    public List<string> RejectAccounts { get; set; } = [];

    /// <summary>The service never answers: the request ends at <c>Payments:TimeoutSeconds</c>.</summary>
    public List<string> TimeoutAccounts { get; set; } = [];
}
