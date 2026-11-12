using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>Who may read the catalogue of input items, its units, groups and settings: whoever views purchase receipts, supplier payments or cultivation cycles (pickers, filters).</summary>
public sealed class InputItemAccess(ZoneAccessGuard zoneAccess)
{
    public async Task<bool> MayReadAsync(CancellationToken cancellationToken)
    {
        foreach (var function in new[] { PermissionFunctions.PurchaseReceipts, PermissionFunctions.SupplierPayments, PermissionFunctions.CultivationCycles })
        {
            if (!(await zoneAccess.PermittedZonesAsync(function, PermissionAction.View, cancellationToken)).IsEmpty)
            {
                return true;
            }
        }

        return false;
    }
}
