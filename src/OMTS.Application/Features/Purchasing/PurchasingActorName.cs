using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Purchasing;

/// <summary>The {user_name} of the purchasing notifications (BR-PURCHASE-05): the person acting.</summary>
internal static class PurchasingActorName
{
    public static async Task<string> OfAsync(ICurrentUser currentUser, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } accountId)
        {
            return "The system";
        }

        var names = await accounts.GetDisplayNamesAsync([accountId], cancellationToken);
        return names.GetValueOrDefault(accountId) ?? "A user";
    }
}
