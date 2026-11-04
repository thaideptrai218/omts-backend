using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>
/// Asks the Payment Notification Service to accept an account, within <c>Payments:TimeoutSeconds</c>. A failure or a
/// timeout comes back as Unavailable, never as an exception: the caller keeps the old account (DEBT-01.0.E2). Only the
/// user's own abort of the request still cancels. Logs carry the masked number.
/// </summary>
public sealed class ReceivingAccountAcceptance(
    IPaymentNotificationService service,
    IOptions<PaymentNotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<ReceivingAccountAcceptance> logger)
{
    public async Task<AccountAcceptanceResult> AskAsync(ReceivingAccountToAccept account, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        AccountAcceptanceResult answer;
        try
        {
            // WaitAsync also ends the wait for a provider that ignores the token.
            answer = await service.AcceptAccountAsync(account, linked.Token).WaitAsync(timeout, timeProvider, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            answer = AccountAcceptanceResult.Unavailable("TIMEOUT");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type: the text of a provider's exception may quote the account number.
            logger.LogError("Payment Notification Service call failed: {ExceptionType}", exception.GetType().Name);
            answer = AccountAcceptanceResult.Unavailable("ERROR");
        }

        if (answer.Outcome != AccountAcceptanceOutcome.Accepted)
        {
            logger.LogWarning(
                "Payment Notification Service did not accept account {Account}: {Outcome} {Reason}",
                Masking.AccountNumber(account.AccountNumber), answer.Outcome, answer.Reason);
        }

        return answer;
    }
}
