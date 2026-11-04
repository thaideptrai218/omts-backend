using OMTS.Application.Common.Messages;
using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// DEBT-01: a receiving bank account through the real service, with its real check by the Payment Notification Service. The
/// number holds letters so that nobody takes it for a real account. An account already set is not touched. No later step
/// needs the account, so when the service does not accept it (<c>Payments:Provider=None</c>, or a real provider that refuses
/// a fictional account) the step is skipped, and a later run tries again: nothing writes an account the service did not accept.
/// </summary>
internal sealed class DemoReceivingBankAccountStep : IDemoSeedStep
{
    public string Name => "receiving bank account";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<ReceivingBankAccountService>();
        var current = DemoSeedContext.Ensure(await service.GetAsync(cancellationToken), "reading the receiving bank account");
        if (current.Account is not null)
        {
            return 0;
        }

        var saved = await service.SaveAsync(
            new SaveReceivingBankAccountRequest("970436", "DEMO00000001", "HTX HAU VAN DON DEMO", ConfirmAccount: true, current.RowVersion),
            cancellationToken);
        if (saved.Error?.Message.Code == MessageCodes.Debt02)
        {
            context.SkipReason = "the Payment Notification Service did not accept the account (MSG-DEBT-02). Set Payments:Provider=Fake in Development to have one.";
            return 0;
        }

        DemoSeedContext.Ensure(saved, "saving the receiving bank account");
        return 1;
    }
}
