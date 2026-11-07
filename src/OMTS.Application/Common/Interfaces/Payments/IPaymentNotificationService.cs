namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>
/// The Payment Notification Service (API-12). No provider is chosen yet, so only <c>None</c> and <c>Fake</c> exist: what
/// a real provider means by "accept" (register the account, or check that it is linked) is decided with the provider.
/// The incoming notifications of transfers (API-13) come with the VietQR sale.
/// </summary>
public interface IPaymentNotificationService
{
    /// <summary>Asks the service to accept the account, that is, to report the transfers made to it (DEBT-01 step 4).</summary>
    /// <returns>The answer. An implementation may also throw or never answer: the caller treats that as not accepted.</returns>
    Task<AccountAcceptanceResult> AcceptAccountAsync(ReceivingAccountToAccept account, CancellationToken cancellationToken = default);
}
