using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// The record rules of purchase receipts on top of the role check the API makes: receipts have no zone and follow the
/// role only (BR-FARM-08), so any Zone Manager views every receipt, drafts of others included (read-only).
/// </summary>
public sealed class PurchaseReceiptAccess(ZoneAccessGuard zoneAccess, ICurrentUser currentUser)
{
    /// <summary>
    /// BR-PURCHASE-03/09: a Draft is changed, completed or removed, and a Completed receipt cancelled, by the person who
    /// recorded it or by the Cooperative Owner.
    /// </summary>
    public async Task<bool> MayChangeAsync(PurchaseReceipt receipt, CancellationToken cancellationToken) =>
        receipt.RecordedByAccountId == currentUser.AccountId || await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>
    /// BR-PURCHASE-01: the rafts offered on a line are those In Use in the zones the user can access, that is the zones of
    /// the pairs whose role records receipts (Zone Manager); the Owner may name any raft.
    /// </summary>
    public Task<PermittedZones> RaftZonesAsync(CancellationToken cancellationToken) =>
        zoneAccess.PairZonesAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, cancellationToken);

    public Task<bool> IsCooperativeOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
}
