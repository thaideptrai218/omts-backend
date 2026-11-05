using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>A Payment Notification Service whose answer a test decides, counting the calls.</summary>
internal sealed class ScriptedPaymentService(Func<ReceivingAccountToAccept, CancellationToken, Task<AccountAcceptanceResult>> answer) : IPaymentNotificationService
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public static ScriptedPaymentService Answering(AccountAcceptanceResult result) => new((_, _) => Task.FromResult(result));

    public Task<AccountAcceptanceResult> AcceptAccountAsync(ReceivingAccountToAccept account, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return answer(account, cancellationToken);
    }
}
