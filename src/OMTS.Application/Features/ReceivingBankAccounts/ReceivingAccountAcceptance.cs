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
        // Not "using": a provider that ignores its token outlives this method and may still look at the token.
        var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.TimeoutSeconds), timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Task<AccountAcceptanceResult>? call = null;
        AccountAcceptanceResult answer;
        try
        {
            // The wait ends on the very token the provider holds, so it is always told, and a provider that ignores it
            // still cannot hold up the answer.
            call = service.AcceptAccountAsync(account, linked.Token);
            answer = await call.WaitAsync(linked.Token);
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
        finally
        {
            DisposeWhenTheCallEnds(call, linked, timeoutSource);
        }

        if (answer.Outcome != AccountAcceptanceOutcome.Accepted)
        {
            logger.LogWarning(
                "Payment Notification Service did not accept account {Account}: {Outcome} {Reason}",
                Masking.ReceivingAccountNumber(account.AccountNumber), answer.Outcome, answer.Reason);
        }

        return answer;
    }

    private static void DisposeWhenTheCallEnds(Task? call, CancellationTokenSource linked, CancellationTokenSource timeoutSource)
    {
        void Dispose()
        {
            linked.Dispose();
            timeoutSource.Dispose();
        }

        if (call is null)
        {
            Dispose();
            return;
        }

        // On the thread pool, not inline: the call often ends inside the Cancel of these very sources.
        _ = call.ContinueWith(
            ended =>
            {
                // Observed: a failure that comes after the user has the answer has nobody left to tell.
                _ = ended.Exception;
                Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
