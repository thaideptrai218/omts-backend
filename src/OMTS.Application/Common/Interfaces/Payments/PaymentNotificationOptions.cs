namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>"Payments" section: how long the Payment Notification Service may take to answer.</summary>
public sealed class PaymentNotificationOptions
{
    public const string SectionName = "Payments";

    /// <summary>After this the call is abandoned and the account is not saved (MSG-DEBT-02). At least 1.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}
