using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Infrastructure.Payments;

/// <summary><c>Payments:Provider = None</c> (and <c>Fake</c> outside Development): no service is connected, so no notification is authentic (401) and nothing is recorded.</summary>
public sealed class UnconfiguredBankNotificationParser : IBankNotificationParser
{
    public bool IsAuthentic(BankNotificationRequest request) => false;

    public BankNotificationParse Parse(BankNotificationRequest request) => BankNotificationParse.Invalid("No Payment Notification Service is configured.");
}
