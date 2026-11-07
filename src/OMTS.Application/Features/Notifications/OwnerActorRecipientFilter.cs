using OMTS.Application.Features.Authorization;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// PM decision (OMTS-216, 03/11): for operational notifications (water, cultivation logs, seeding, harvest) the
/// Cooperative Owner is not told of their own action. Money notifications (purchases, payments, collections,
/// wages) do NOT use this filter: they are always sent, also to the Owner who did the action.
/// </summary>
public sealed class OwnerActorRecipientFilter(PermissionService permissions)
{
    /// <summary>
    /// The recipients without the actor when the actor is a Cooperative Owner. Staff actors stay (a Zone Manager
    /// still gets the safety alert of their own reading); with no actor (jobs, system) nobody is removed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> WithoutOwnerActorAsync(
        IReadOnlyList<Guid> recipientAccountIds, Guid? actorAccountId, CancellationToken cancellationToken)
    {
        if (actorAccountId is not { } actorId || !recipientAccountIds.Contains(actorId))
        {
            return recipientAccountIds;
        }

        var access = await permissions.GetAccessAsync(actorId, cancellationToken);
        return access is { IsCooperativeOwner: true } ? recipientAccountIds.Where(id => id != actorId).ToList() : recipientAccountIds;
    }
}
