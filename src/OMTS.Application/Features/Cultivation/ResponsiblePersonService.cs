using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Cultivation;

/// <summary>A person who can be named as responsible for a nursery batch or a seeding session of a zone.</summary>
public sealed record ResponsibleOption(Guid AccountId, string Name, bool IsSelf);

/// <summary>
/// "Người phụ trách" of nursery and seeding (owner decision of 26/11/2026): a label on the record only, with no task, notification or confirmation. Anyone may name themself; the
/// Cooperative Owner may also name an active Zone Manager of the zone.
/// </summary>
public sealed class ResponsiblePersonService(ICurrentUser currentUser, IZoneRecipientQuery recipients, IAccountRepository accounts, ZoneAccessGuard zoneAccess)
{
    public const string Field = "ResponsibleAccountId";

    public async Task<Result<IReadOnlyList<ResponsibleOption>>> OptionsAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var allowed = await AllowedAsync(zoneId, cancellationToken);
        var names = await accounts.GetDisplayNamesAsync(allowed, cancellationToken);
        IReadOnlyList<ResponsibleOption> options = allowed.Select(id => new ResponsibleOption(id, names.GetValueOrDefault(id) ?? string.Empty, id == currentUser.AccountId)).ToList();
        return Result<IReadOnlyList<ResponsibleOption>>.Success(options);
    }

    /// <summary>The value to store: <paramref name="requested"/> left out keeps <paramref name="current"/>, and keeping the same person is always accepted.</summary>
    public async Task<(Guid? AccountId, AppError? Error)> ReadAsync(Guid zoneId, Guid? requested, Guid? current, CancellationToken cancellationToken)
    {
        if (requested is null || requested == current)
        {
            return (current, null);
        }

        return (await AllowedAsync(zoneId, cancellationToken)).Contains(requested.Value)
            ? (requested, null)
            : (null, AppError.Invalid(Field, "Choose yourself or a zone manager of this zone."));
    }

    public async Task<string?> NameAsync(Guid? accountId, CancellationToken cancellationToken) =>
        accountId is { } id ? (await accounts.GetDisplayNamesAsync([id], cancellationToken)).GetValueOrDefault(id) : null;

    private async Task<IReadOnlyList<Guid>> AllowedAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } actor)
        {
            return [];
        }

        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        if (!owners.Contains(actor))
        {
            return [actor];
        }

        // The query answers with the owners when the zone has no manager: they are not offered a second time.
        var managers = await recipients.GetZoneManagersOrOwnersAsync(zoneId, cancellationToken);
        return [actor, .. managers.Except(owners)];
    }
}
