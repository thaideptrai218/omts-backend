using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Infrastructure.Payments;

/// <summary>
/// <c>Payments:Provider = None</c>, the default until a provider is chosen: no account is accepted, so the Owner gets
/// MSG-DEBT-02, the old account stays, and VietQR stays out of use instead of showing transfers nobody would report.
/// </summary>
internal sealed class UnconfiguredPaymentNotificationService : IPaymentNotificationService
{
    public Task<AccountAcceptanceResult> AcceptAccountAsync(ReceivingAccountToAccept account, CancellationToken cancellationToken = default) =>
        Task.FromResult(AccountAcceptanceResult.Unavailable("NOT_CONFIGURED"));
}
