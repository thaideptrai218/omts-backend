using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>The receipts a caller sees: every one (<see cref="All"/>) or only those of <see cref="ZoneIds"/>; a receipt with no zone is in no zone.</summary>
public sealed record ReceiptScope(bool All, IReadOnlySet<Guid> ZoneIds)
{
    public static readonly ReceiptScope Everything = new(true, new HashSet<Guid>());

    public bool Sees(Guid? zoneId) => All || (zoneId is { } zone && ZoneIds.Contains(zone));

    /// <summary>The zones to filter a list by: the zones asked for, limited to those the caller sees; null when nothing limits the list (empty: nothing is seen).</summary>
    public IReadOnlyList<Guid>? Limit(IReadOnlyList<Guid>? asked) =>
        All ? asked : asked is null ? ZoneIds.ToList() : asked.Where(ZoneIds.Contains).ToList();
}

/// <summary>
/// The record rules of purchase receipts and returns on top of the role check the API makes. Owner decision of 25/11/2026: the Owner and the Accountant
/// (view and export) see every receipt; a Zone Manager sees and writes only the receipts of the zones of its pairs, and a receipt with no zone is
/// the Owner's and the Accountant's alone. A Zone Manager must name one of its zones on a receipt it records.
/// </summary>
public sealed class PurchaseReceiptAccess(ZoneAccessGuard zoneAccess, ICurrentUser currentUser, PermissionService permissions)
{
    /// <summary>
    /// BR-PURCHASE-03/09: a Draft is changed, completed or removed, and a Completed receipt cancelled, by the person who
    /// recorded it or by the Cooperative Owner.
    /// </summary>
    public async Task<bool> MayChangeAsync(PurchaseReceipt receipt, CancellationToken cancellationToken) =>
        receipt.RecordedByAccountId == currentUser.AccountId || await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>The same rule for a purchase return.</summary>
    public async Task<bool> MayChangeAsync(PurchaseReturn purchaseReturn, CancellationToken cancellationToken) =>
        purchaseReturn.RecordedByAccountId == currentUser.AccountId || await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>
    /// BR-PURCHASE-01: the rafts offered on a line are those In Use in the zones the user can access, that is the zones of
    /// the pairs whose role records receipts (Zone Manager); the Owner may name any raft.
    /// </summary>
    public Task<PermittedZones> RaftZonesAsync(CancellationToken cancellationToken) =>
        zoneAccess.PairZonesAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, cancellationToken);

    public Task<bool> IsCooperativeOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>
    /// What the caller sees: everything for the Owner and for a role that views receipts without recording them (the Accountant), a pair at All zones too;
    /// for a Zone Manager the zones of its pairs.
    /// </summary>
    public async Task<ReceiptScope> ScopeAsync(CancellationToken cancellationToken)
    {
        if (await zoneAccess.IsCooperativeOwnerAsync(cancellationToken))
        {
            return ReceiptScope.Everything;
        }

        var access = currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;
        var zones = new HashSet<Guid>();
        foreach (var grant in access?.Grants ?? [])
        {
            if (grant.Permissions.FirstOrDefault(p => p.FunctionCode == PermissionFunctions.PurchaseReceipts) is not { CanView: true } receipts)
            {
                continue;
            }

            // A pair at All zones, or of a role that cannot record receipts (it only reads and exports them), sees every receipt.
            if (grant.ZoneId is not { } zoneId || !receipts.CanCreate)
            {
                return ReceiptScope.Everything;
            }

            zones.Add(zoneId);
        }

        return new ReceiptScope(false, zones);
    }

    /// <summary>The caller sees a receipt (or return) of this zone; a Zone Manager does not see one of another zone or with no zone.</summary>
    public async Task<bool> SeesAsync(Guid? zoneId, CancellationToken cancellationToken) => (await ScopeAsync(cancellationToken)).Sees(zoneId);

    /// <summary>
    /// The caller may write (<paramref name="action"/>) a receipt or return of this zone: the Owner always; a Zone Manager only with a zone of a pair that holds the action (MSG-GEN-01 "Zone" when it names none,
    /// 403 for another zone). Null when allowed.
    /// </summary>
    public async Task<AppError?> RefuseWriteAsync(Guid? zoneId, PermissionAction action, CancellationToken cancellationToken)
    {
        if (await zoneAccess.IsCooperativeOwnerAsync(cancellationToken))
        {
            return null;
        }

        if (zoneId is not { } zone)
        {
            return AppError.Invalid("Zone");
        }

        return (await zoneAccess.PairZonesAsync(PermissionFunctions.PurchaseReceipts, action, cancellationToken)).Allows(zone) ? null : AppError.Forbidden;
    }
}
