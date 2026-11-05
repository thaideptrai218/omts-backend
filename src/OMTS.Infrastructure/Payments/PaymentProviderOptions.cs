namespace OMTS.Infrastructure.Payments;

/// <summary>
/// "Payments" section: the provider of the Payment Notification Service. No real provider is chosen yet. <c>None</c>, the
/// default, accepts no account (MSG-DEBT-02). <c>Fake</c> works in Development only: elsewhere it acts as <c>None</c>, so a
/// deployment never reports an account as accepted by a service nobody asked.
/// </summary>
public sealed class PaymentProviderOptions
{
    public const string SectionName = "Payments";
    public const string FakeProvider = "Fake";
    public const string NoneProvider = "None";

    public string Provider { get; set; } = NoneProvider;
    public FakePaymentOptions Fake { get; set; } = new();
}
