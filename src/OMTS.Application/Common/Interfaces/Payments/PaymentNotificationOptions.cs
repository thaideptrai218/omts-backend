namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>"Payments" section: how long the Payment Notification Service may take to answer.</summary>
public sealed class PaymentNotificationOptions
{
    public const string SectionName = "Payments";

    public const int MinTimeoutSeconds = 1;

    /// <summary>The user waits for this answer in one request: longer than 5 minutes no proxy or person keeps waiting.</summary>
    public const int MaxTimeoutSeconds = 300;

    /// <summary>
    /// After this the call is abandoned and the account is not saved (MSG-DEBT-02). A value outside
    /// <see cref="MinTimeoutSeconds"/> to <see cref="MaxTimeoutSeconds"/> stops the host at startup.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;
}
