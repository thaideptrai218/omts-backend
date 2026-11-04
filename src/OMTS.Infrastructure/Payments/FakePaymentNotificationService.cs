using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Infrastructure.Payments;

/// <summary>
/// Stands in for the Payment Notification Service until a provider is chosen: nothing leaves the server. Accepts every
/// account except those of <see cref="FakePaymentOptions"/>. Logs carry the masked number.
/// </summary>
public sealed class FakePaymentNotificationService(IOptions<PaymentProviderOptions> options, ILogger<FakePaymentNotificationService> logger)
    : IPaymentNotificationService
{
    public async Task<AccountAcceptanceResult> AcceptAccountAsync(ReceivingAccountToAccept account, CancellationToken cancellationToken = default)
    {
        var fake = options.Value.Fake;
        if (fake.TimeoutAccounts.Contains(account.AccountNumber, StringComparer.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (fake.RejectAccounts.Contains(account.AccountNumber, StringComparer.OrdinalIgnoreCase))
        {
            return AccountAcceptanceResult.Rejected("FAKE_CONFIGURED_REFUSAL");
        }

        logger.LogInformation("Fake payment notification service accepted account {Account}", Masking.AccountNumber(account.AccountNumber));
        return AccountAcceptanceResult.Accepted;
    }
}
