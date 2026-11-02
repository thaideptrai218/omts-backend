using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
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
/// FARM-05 – the Cooperative Owner grants or revokes zone–role pairs of staff accounts: one fixed role (Zone Manager
/// or Accountant, BR-FARM-07) in one zone or in All zones. A pair is Effective until it is revoked; re-granting creates
/// a new pair and a revoked one is never restored (BR-FARM-08). Changes apply on the next request (permission cache
/// interceptor).
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

    /// <summary>Effective and revoked pairs of a staff account, newest first, with who granted and revoked them.</summary>
    public async Task<Result<IReadOnlyList<GrantRow>>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (await accounts.GetByIdAsync(accountId, cancellationToken) is not { SubjectType: SubjectType.Staff })
        {
            return AppError.NotFound;
        }

        return Result<IReadOnlyList<GrantRow>>.Success(await RowsAsync(accountId, cancellationToken));
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

        var grant = added.Value![0];
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A double click: the same pair was saved meanwhile (unique Effective pair index). Answer as the pre-check
            // would, with the pair that won.
            var existing = await grants.FindDuplicateAsync(accountId, grant.RoleId, grant.ZoneId, cancellationToken)
                ?? throw new InvalidOperationException("The unique pair index refused a pair that has no Effective twin.");
            return AppError.AlreadyExists("Zone-role pair", existing.Id);
        }

        return await RowAsync(accountId, grant.Id, cancellationToken);
    }

    /// <summary>
    /// Validates the rows and adds them to the unit of work (used by grant, staff account creation and LAB-01): the
    /// account is Active (FARM-05.0.E2, MSG-GEN-09), the role is a fixed role, the zone is In Use (a discontinued zone
    /// is MSG-GEN-09), and the account does not already hold the role in that zone or for All zones (FARM-05.0.E1,
    /// MSG-GEN-02 with the existing pair).
    /// </summary>
    public async Task<Result<IReadOnlyList<AccessGrant>>> AddGrantsAsync(Account account, IReadOnlyList<GrantDraft> drafts, CancellationToken cancellationToken)
    {
        if (account.StatusId != StatusIds.Account.Active)
        {
            return AppError.Conflict(UserMessages.WrongStatus("Account", statusCatalog.DisplayNameOf(account.StatusId)));
        }

        var zoneIds = drafts.Where(d => d.ZoneId is not null).Select(d => d.ZoneId!.Value).Distinct().ToList();
        var zonesById = (await zones.GetByIdsAsync(zoneIds, cancellationToken)).ToDictionary(z => z.Id);
        var added = new List<AccessGrant>();
        foreach (var draft in drafts)
        {
            var resolved = await ResolveRoleAsync(draft, cancellationToken);
            if (!resolved.IsSuccess)
            {
                return resolved.Error!;
            }

            var role = resolved.Value!.Role;
            if (draft.ZoneId is { } zoneId)
            {
                if (!zonesById.TryGetValue(zoneId, out var zone))
                {
                    return AppError.Invalid("Zone");
                }

                if (zone.StatusId != StatusIds.Usage.InUse)
                {
                    return AppError.NotUsableChoice($"Farming zone {zone.Code}", statusCatalog.DisplayNameOf(zone.StatusId), "Zone");
                }
            }

            // BR-FARM-08 (A): a pair for one zone duplicates a pair of the same role for All zones. Under a race, the unique
            // Effective pair index stops a second copy of the same pair; a one-zone pair saved at the same moment as an
            // All-zones pair of the same role is accepted (no lock), the Owner can revoke the redundant one.
            var duplicate = added.FirstOrDefault(g => g.RoleId == role.Id && (g.ZoneId is null || g.ZoneId == draft.ZoneId))
                ?? await grants.FindDuplicateAsync(account.Id, role.Id, draft.ZoneId, cancellationToken);
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
                GrantedByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Grants are made by a logged-in Owner."),
            };
            grants.Add(grant);
            audit.Record(new AuditEntry("AccessGrant.Granted", nameof(AccessGrant))
            {
                RecordId = grant.Id,
                RecordCode = account.Username,
                ZoneId = grant.ZoneId,
                After = new { AccountId = account.Id, Role = role.Code, grant.ZoneId },
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

    /// <summary>What the draft rows would allow; computed from the fixed matrix by the same rules that enforce permissions.</summary>
    public async Task<Result<IReadOnlyList<FunctionAccess>>> PreviewAsync(PermissionPreviewRequest request, CancellationToken cancellationToken)
    {
        var zoneGrants = new List<ZoneGrant>();
        foreach (var draft in request.Grants ?? [])
        {
            var resolved = await ResolveRoleAsync(draft, cancellationToken);
            if (!resolved.IsSuccess)
            {
                return resolved.Error!;
            }

            var (role, fixedRole) = resolved.Value!;
            zoneGrants.Add(new ZoneGrant(draft.ZoneId, role.Id, role.Code, role.Name, fixedRole.GrantedPermissions));
        }

        return Result<IReadOnlyList<FunctionAccess>>.Success(
            new AccountAccess(Guid.Empty, SubjectType.Staff, false, true, zoneGrants).DescribeFunctions());
    }

    /// <summary>
    /// The fixed role row named by id or by code (both must agree when both are sent); anything else is MSG-GEN-01
    /// "Role".
    /// </summary>
    private async Task<Result<(Role Role, FixedRole Fixed)>> ResolveRoleAsync(GrantDraft draft, CancellationToken cancellationToken)
    {
        var byCode = FixedRoles.FindByCode(draft.Role);
        if (!string.IsNullOrWhiteSpace(draft.Role) && byCode is null)
        {
            return AppError.Invalid("Role");
        }

        var role = draft.RoleId is { } roleId
            ? await roles.GetByIdAsync(roleId, cancellationToken)
            : byCode is null ? null : await roles.FindByCodeAsync(byCode.Code, cancellationToken);
        if (role is null || FixedRoles.FindByCode(role.Code) is not { } fixedRole || (byCode is not null && byCode != fixedRole))
        {
            return AppError.Invalid("Role");
        }

        return (role, fixedRole);
    }

    private async Task<GrantRow> RowAsync(Guid accountId, Guid grantId, CancellationToken cancellationToken) =>
        (await RowsAsync(accountId, cancellationToken)).Single(g => g.Id == grantId);

    // Re-read with role and zone, and the names of the granting and revoking persons.
    private async Task<IReadOnlyList<GrantRow>> RowsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var list = await grants.ListForAccountAsync(accountId, cancellationToken);
        var personIds = list.Select(g => g.GrantedByAccountId)
            .Concat(list.Where(g => g.RevokedByAccountId is not null).Select(g => g.RevokedByAccountId!.Value))
            .Distinct()
            .ToList();
        var names = await accounts.GetDisplayNamesAsync(personIds, cancellationToken);
        return list.Select(g => ToRow(g, names)).ToList();
    }

    private static GrantRow ToRow(AccessGrant g, IReadOnlyDictionary<Guid, string> names) => new(
        g.Id,
        g.ZoneId,
        g.Zone?.Name,
        g.ZoneId is null,
        g.RoleId,
        g.Role?.Code ?? string.Empty,
        g.Role?.Name ?? string.Empty,
        g.StatusId == StatusIds.AccessGrant.Revoked ? "Revoked" : "Effective",
        g.CreatedAt,
        g.GrantedByAccountId,
        names.GetValueOrDefault(g.GrantedByAccountId),
        g.RevokedAt,
        g.RevokedByAccountId,
        g.RevokedByAccountId is { } revokedBy ? names.GetValueOrDefault(revokedBy) : null,
        g.RevokeReason);
}
