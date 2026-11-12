using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// Who may do what with the orders: the Owner everything; a Zone Manager what its role holds (an order has no zone, so the right is the same for every order); the Accountant reads and exports. Invoices made from an
/// order stay zone-scoped by the lots they take.
/// </summary>
public sealed class SalesOrderAccess(ZoneAccessGuard zoneAccess)
{
    public Task<bool> IsOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>The caller holds <paramref name="action"/> on SALES_ORDERS (any pair of a role holding it, as the function is cooperative-wide); the Owner always.</summary>
    public async Task<bool> MayAsync(PermissionAction action, CancellationToken cancellationToken) =>
        await IsOwnerAsync(cancellationToken) || !(await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesOrders, action, cancellationToken)).IsEmpty;

    /// <summary>
    /// May the caller take an advance on an order: SALES_ORDERS Edit, or CUSTOMER_PAYMENTS Create (the Accountant).
    /// </summary>
    public async Task<bool> MayTakeAdvanceAsync(CancellationToken cancellationToken) =>
        await MayAsync(PermissionAction.Edit, cancellationToken)
        || !(await zoneAccess.PermittedZonesAsync(PermissionFunctions.CustomerPayments, PermissionAction.Create, cancellationToken)).IsEmpty;

    /// <summary>The buttons the caller gets on an order.</summary>
    public async Task<SalesOrderCaller> CallerAsync(CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return new SalesOrderCaller(true, true, true, true, true, true);
        }

        return new SalesOrderCaller(
            false,
            await MayAsync(PermissionAction.Edit, cancellationToken),
            await MayAsync(PermissionAction.Delete, cancellationToken),
            await MayAsync(PermissionAction.Export, cancellationToken),
            await MayTakeAdvanceAsync(cancellationToken),
            !(await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken)).IsEmpty);
    }
}
