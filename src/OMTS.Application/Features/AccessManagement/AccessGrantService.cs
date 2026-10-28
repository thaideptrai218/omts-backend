using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// FARM-05 – the Cooperative Owner grants or revokes zone–role pairs of staff accounts. Effective
/// permissions are the union of the roles, each limited to its zone; re-granting creates a new row and a
/// revoked row is never restored. Changes apply on the next request (permission cache interceptor).
/// </summary>
public sealed class AccessGrantService(
    IAccountRepository accounts,
    IAccessGrantRepository grants,
    IRoleRepository roles,
    IFarmingZoneRepository zones,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxReasonLength = 1000;

    public async Task<Result<IReadOnlyList<GrantRow>>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (await accounts.GetByIdAsync(accountId, cancellationToken) is not { SubjectType: SubjectType.Staff })
        {
            return AppError.NotFound;
        }

        var today = timeProvider.Today();
        return (await grants.ListForAccountAsync(accountId, cancellationToken)).Select(g => ToRow(g, today)).ToList();
    }

    public async Task<Result<GrantRow>> GrantAsync(Guid accountId, GrantDraft draft, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (account.SubjectType != SubjectType.Staff)
        {
            return AppError.Forbidden;
        }

        var added = await AddGrantsAsync(account, [draft], cancellationToken);
        if (!added.IsSuccess)
        {
            return added.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await RowAsync(accountId, added.Value![0].Id, cancellationToken);
    }

    /// <summary>
    /// Validates the rows and adds them to the unit of work (used by grant, staff account creation and
    /// LAB-01). Zone and role must be In Use (a discontinued zone is MSG-GEN-09); overlapping Effective duplicates
    /// are refused (MSG-GEN-02).
    /// </summary>
    public async Task<Result<IReadOnlyList<AccessGrant>>> AddGrantsAsync(Account account, IReadOnlyList<GrantDraft> drafts, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var zoneIds = drafts.Where(d => d.ZoneId is not null).Select(d => d.ZoneId!.Value).Distinct().ToList();
        var zonesById = (await zones.GetByIdsAsync(zoneIds, cancellationToken)).ToDictionary(z => z.Id);
        var added = new List<AccessGrant>();
        foreach (var draft in drafts)
        {
            var role = await roles.GetByIdAsync(draft.RoleId, cancellationToken);
            if (role is null || role.StatusId != StatusIds.Usage.InUse)
            {
                return AppError.Invalid("Role");
            }

            if (draft.ZoneId is { } zoneId)
            {
                if (!zonesById.TryGetValue(zoneId, out var zone))
                {
                    return AppError.Invalid("Zone");
                }

                if (zone.StatusId != StatusIds.Usage.InUse)
                {
                    return new AppError(
                        ErrorKind.Validation,
                        UserMessages.WrongStatus($"Farming zone {zone.Code}", statusCatalog.DisplayNameOf(zone.StatusId)) with { LegacyCode = MessageCodes.Legacy.Msg01 },
                        new Dictionary<string, object?> { ["field"] = "Zone" });
                }
            }

            var from = draft.EffectiveFrom ?? today;
            if (draft.EffectiveTo is { } to && to < from)
            {
                return AppError.Invalid("Effective to");
            }

            var duplicate = added.FirstOrDefault(g => g.ZoneId == draft.ZoneId && g.RoleId == draft.RoleId && Overlaps(g, from, draft.EffectiveTo))
                ?? await grants.FindOverlappingAsync(account.Id, draft.ZoneId, draft.RoleId, from, draft.EffectiveTo, cancellationToken);
            if (duplicate is not null)
            {
                return AppError.AlreadyExists("Zone-role pair", duplicate.Id);
            }

            var grant = new AccessGrant
            {
                AccountId = account.Id,
                RoleId = role.Id,
                ZoneId = draft.ZoneId,
                ScopeType = draft.ZoneId is null ? AccessScopeType.AllZones : AccessScopeType.SpecificZone,
                EffectiveFrom = from,
                EffectiveTo = draft.EffectiveTo,
                GrantedByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Grants are made by a logged-in Owner."),
            };
            grants.Add(grant);
            audit.Record(new AuditEntry("AccessGrant.Granted", nameof(AccessGrant))
            {
                RecordId = grant.Id,
                RecordCode = account.Username,
                ZoneId = grant.ZoneId,
                After = new { AccountId = account.Id, Role = role.Name, grant.ZoneId, grant.EffectiveFrom, grant.EffectiveTo },
            });
            added.Add(grant);
        }

        return added;
    }

    public async Task<Result<GrantRow>> RevokeAsync(Guid accountId, Guid grantId, ReasonRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        var grant = await grants.GetAsync(grantId, accountId, cancellationToken);
        if (grant is null)
        {
            return AppError.NotFound;
        }

        if (grant.StatusId == StatusIds.AccessGrant.Revoked)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Zone-role pair", statusCatalog.DisplayNameOf(grant.StatusId)));
        }

        grant.StatusId = StatusIds.AccessGrant.Revoked;
        grant.RevokedAt = timeProvider.GetUtcNow();
        grant.RevokedByAccountId = currentUser.AccountId;
        grant.RevokeReason = reason;
        audit.RecordStatusChange(nameof(AccessGrant), grant.Id, null, StatusIds.AccessGrant.Effective, StatusIds.AccessGrant.Revoked, reason, grant.ZoneId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await RowAsync(accountId, grantId, cancellationToken);
    }

    // Re-read with role and zone names for the response.
    private async Task<GrantRow> RowAsync(Guid accountId, Guid grantId, CancellationToken cancellationToken) =>
        ToRow((await grants.ListForAccountAsync(accountId, cancellationToken)).Single(g => g.Id == grantId), timeProvider.Today());

    /// <summary>What the draft rows would allow; computed by the same rules that enforce permissions.</summary>
    public async Task<Result<IReadOnlyList<FunctionAccess>>> PreviewAsync(PermissionPreviewRequest request, CancellationToken cancellationToken)
    {
        var zoneGrants = new List<ZoneGrant>();
        foreach (var draft in request.Grants ?? [])
        {
            var role = await roles.GetByIdAsync(draft.RoleId, cancellationToken);
            if (role is null || role.StatusId != StatusIds.Usage.InUse)
            {
                return AppError.Invalid("Role");
            }

            zoneGrants.Add(new ZoneGrant(draft.ZoneId, role.Id, role.Name, role.Permissions.Select(ToGranted).ToList()));
        }

        return Result<IReadOnlyList<FunctionAccess>>.Success(
            new AccountAccess(Guid.Empty, SubjectType.Staff, false, true, zoneGrants).DescribeFunctions());
    }

    public static GrantedPermission ToGranted(RolePermission p) =>
        new(p.FunctionCode, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.OtherPermissions, p.DataScope);

    public static GrantRow ToRow(AccessGrant g, DateOnly today) => new(
        g.Id,
        g.ZoneId,
        g.Zone?.Name,
        g.ZoneId is null,
        g.RoleId,
        g.Role?.Name ?? string.Empty,
        g.EffectiveFrom,
        g.EffectiveTo,
        g.StatusId == StatusIds.AccessGrant.Revoked ? "Revoked"
            : g.EffectiveTo < today ? "Expired"
            : g.EffectiveFrom > today ? "NotYetEffective"
            : "Effective",
        g.RevokedAt,
        g.RevokeReason);

    private static bool Overlaps(AccessGrant grant, DateOnly from, DateOnly? to) =>
        (to is null || grant.EffectiveFrom <= to) && (grant.EffectiveTo is null || grant.EffectiveTo >= from);
}
