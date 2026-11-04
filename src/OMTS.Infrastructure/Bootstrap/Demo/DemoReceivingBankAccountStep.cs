using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// DEBT-01: a receiving bank account through the real service and the fake Payment Notification Service. The number holds
/// letters so that nobody takes it for a real account. An account already set is not touched.
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

        DemoSeedContext.Ensure(
            await service.SaveAsync(
                new SaveReceivingBankAccountRequest("970436", "DEMO00000001", "HTX HAU VAN DON DEMO", ConfirmAccount: true, current.RowVersion),
                cancellationToken),
            "saving the receiving bank account");
        return 1;
    }
}
