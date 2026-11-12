using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The order an invoice is made from, read tracked: its advances (what is left of each), what its other invoices delivered and the discount they took.</summary>
/// <param name="Advances">The advances of the order that count, oldest first, with their allocations.</param>
/// <param name="DeliveredByOthers">The quantity of each order line delivered by the invoices that are not cancelled, other than this one.</param>
/// <param name="DiscountShared">The discount of the invoices of the order that are not cancelled, other than this one.</param>
internal sealed record SalesInvoiceOrderFacts(SalesOrder Order, IReadOnlyList<CustomerPayment> Advances, IReadOnlyDictionary<Guid, decimal> DeliveredByOthers, decimal DiscountShared)
{
    /// <summary>What is left of the advances of the order for another invoice of it.</summary>
    public decimal AdvanceLeft => Advances.Sum(Orders.SalesOrderViews.Left);
}

/// <summary>What the entries name, read tracked as the save will write it, and the debt of the customer before the invoice (BR-SALE-07 (4)).</summary>
/// <param name="DebtBefore">What the customer owes apart from the advances reserved for open orders (they pay no other invoice): above 0 it is a debt, below 0 free credit.</param>
/// <param name="Resolved">The unit, the quantity and the order line of each line, by line number.</param>
/// <param name="Order">Null for a sale that was awaiting its transfer before invoices came from orders.</param>
/// <param name="Standing">Where the customer stands against the debt limit; null for a walk-in customer, or while the debt warning is off.</param>
/// <param name="AllowMoreDebtOverLimit">False: an invoice that leaves a debt for a customer over the limit is refused.</param>
internal sealed record SalesInvoiceFacts(
    Customer? Customer, PurchaseRequest? Request, IReadOnlyDictionary<Guid, Product> Products, IReadOnlyList<HarvestLot> Lots, Guid ZoneId, bool ByOwner, decimal DebtBefore,
    IReadOnlyDictionary<int, ResolvedLine> Resolved, SalesInvoiceOrderFacts? Order = null, CustomerDebtStanding? Standing = null, bool AllowMoreDebtOverLimit = true);

/// <summary>
/// SALE-05 steps 2 to 6 against the database: the profile, the request (SALE-05.3), the products In Use (GBR-01), the lots for sale in zones the user can access (BR-SALE-06, BR-FARM-08)
/// and the zone of the invoice (BR-SALE-08). The lots are marked changed, so a parallel close or sale of one of them makes one of the two saves fail.
/// </summary>
public sealed class SalesInvoiceFactsReader(
    ICustomerRepository customers,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    ISalesOrderRepository orders,
    IProductRepository products,
    IHarvestLotRepository lots,
    IFarmingZoneRepository zones,
    HarvestLotStatusService lotStatuses,
    ZoneAccessGuard zoneAccess,
    CustomerDebtReader debts,
    CustomerDebtStandings standings,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>A request is offered when its pickup date is not more than 3 days ago (BR-SALE-01).</summary>
    public const int RequestPickupDaysBack = 3;

    internal async Task<Result<SalesInvoiceFacts>> ReadAsync(SalesInvoiceEntries entries, CancellationToken cancellationToken)
    {
        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken);
        if (permitted.IsEmpty)
        {
            return AppError.Forbidden;
        }

        var order = await ReadOrderAsync(entries, cancellationToken);
        if (!order.IsSuccess)
        {
            return order.Error!;
        }

        var orderFacts = order.Value;
        // The customer and the zone are the order's; an invoice of the old flow (a sale that was awaiting its transfer) names its own.
        var chosenCustomer = orderFacts is null ? await ReadCustomerAsync(entries.CustomerId, cancellationToken) : OrderCustomer(orderFacts.Order);
        if (!chosenCustomer.IsSuccess)
        {
            return chosenCustomer.Error!;
        }

        var customer = chosenCustomer.Value;
        var request = await ReadRequestAsync(entries, customer, cancellationToken);
        if (!request.IsSuccess)
        {
            return request.Error!;
        }

        var lineProducts = await ReadProductsAsync(entries.Lines, new HashSet<Guid>(), cancellationToken);
        if (!lineProducts.IsSuccess)
        {
            return lineProducts.Error!;
        }

        var resolved = SalesInvoiceLineResolver.Resolve(entries.Lines, lineProducts.Value!, orderFacts?.Order, []);
        if (!resolved.IsSuccess)
        {
            return resolved.Error!;
        }

        var lineLots = await ReadLotsAsync(entries, permitted, cancellationToken);
        if (!lineLots.IsSuccess)
        {
            return lineLots.Error!;
        }

        // An invoice made from an order takes the zone of its first lot (an order has no zone); one of the old flow takes the zone it names.
        var zoneId = orderFacts is null ? entries.ZoneId ?? lineLots.Value![0].Raft.ZoneId : lineLots.Value![0].Raft.ZoneId;
        if (orderFacts is null && entries.ZoneId is { } chosen && (await zones.GetByIdsAsync([chosen], cancellationToken)).Count == 0)
        {
            return AppError.Invalid("Zone");
        }

        if (!permitted.Allows(zoneId))
        {
            return AppError.Forbidden;
        }

        // SALE-05.0.E2: a lot closed after it was chosen, named; checked last, as a fix of the fields above would not change it.
        if (lotStatuses.EnsureForSale(lineLots.Value!) is { } closed)
        {
            return closed;
        }

        var byOwner = await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        var debt = customer is null ? 0m : (await debts.AllocationAsync(customer.Id, cancellationToken)).DebtApartFromReserved;
        var policy = await standings.PolicyAsync(cancellationToken);
        var standing = customer is not null && policy.Enabled ? (await standings.ForAsync([customer.Id], policy, cancellationToken))[customer.Id] : null;
        return new SalesInvoiceFacts(customer, request.Value, lineProducts.Value!, lineLots.Value!, zoneId, byOwner, debt, resolved.Value!, orderFacts, standing, policy.AllowMoreOverLimit);
    }

    /// <summary>
    /// The order of the invoice, tracked with its advances (MSG-GEN-01 when it does not exist, 409 MSG-ORDER-05 when it is not Draft or Delivering, 400 MSG-ORDER-06 when the request names another customer);
    /// null with no order. The order is marked changed in the save, so a parallel invoice, finish or cancel of the same order makes one of the two saves fail and read again.
    /// </summary>
    private async Task<Result<SalesInvoiceOrderFacts?>> ReadOrderAsync(SalesInvoiceEntries entries, CancellationToken cancellationToken)
    {
        if (entries.SalesOrderId is not { } orderId)
        {
            return Result<SalesInvoiceOrderFacts?>.Success(null);
        }

        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null)
        {
            return AppError.Invalid("Sales order");
        }

        if (!SalesOrderRules.IsOpen(order.StatusId))
        {
            return AppError.Conflict(UserMessages.OrderNotOpenForInvoice(order.Code, statusCatalog.DisplayNameOf(order.StatusId)));
        }

        if ((entries.CustomerId is not null && entries.CustomerId != order.CustomerId) || (entries.WalkInRequested && order.CustomerId is not null))
        {
            return OrderMismatch(order, "Customer");
        }

        var advances = order.CustomerId is null ? [] : await orders.CountedAdvancesForUpdateAsync(order.Id, cancellationToken);
        var delivered = await orders.DeliveredByLineAsync([order.Id], null, cancellationToken);
        var shared = (await orders.InvoicesOfAsync(order.Id, cancellationToken)).Where(i => i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => i.DiscountAmount);
        return new SalesInvoiceOrderFacts(order, advances, delivered, shared);
    }

    private static AppError OrderMismatch(SalesOrder order, string field) =>
        new(ErrorKind.Validation, UserMessages.InvoiceDiffersFromOrder(order.Code, field), new Dictionary<string, object?> { ["field"] = field });

    /// <summary>The customer of the order, tracked; MSG-GEN-09 when it was merged into another (an order is moved with its customer, so this only catches a merge in progress).</summary>
    private Result<Customer?> OrderCustomer(SalesOrder order) =>
        order.Customer is null
            ? Result<Customer?>.Success(null)
            : order.Customer.MergedIntoCustomerId is null
                ? order.Customer
                : AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {order.Customer.Name}", CustomerService.MergedStatus));

    /// <summary>The profile chosen for an invoice, tracked: null without a choice; MSG-GEN-01 when it does not exist, MSG-GEN-09 when it was merged into another.</summary>
    internal async Task<Result<Customer?>> ReadCustomerAsync(Guid? customerId, CancellationToken cancellationToken)
    {
        if (customerId is null)
        {
            return Result<Customer?>.Success(null);
        }

        var customer = await customers.GetForEditAsync(customerId.Value, cancellationToken);
        if (customer is null)
        {
            return AppError.Invalid("Customer");
        }

        return customer.MergedIntoCustomerId is null
            ? customer
            : AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus));
    }

    private async Task<Result<PurchaseRequest?>> ReadRequestAsync(SalesInvoiceEntries entries, Customer? customer, CancellationToken cancellationToken)
    {
        if (entries.RequestId is not { } requestId)
        {
            return Result<PurchaseRequest?>.Success(null);
        }

        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        if (request is null || request.CustomerId != customer?.Id)
        {
            return AppError.Invalid("Purchase request");
        }

        if (request.StatusId != StatusIds.PurchaseRequest.Accepted)
        {
            return AppError.Conflict(UserMessages.WrongStatus(PurchaseRequestRules.ObjectName, statusCatalog.DisplayNameOf(request.StatusId)));
        }

        if ((await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id))
        {
            return AppError.Conflict(UserMessages.WrongStatus(PurchaseRequestRules.ObjectName, PurchaseRequestRules.FulfilledStatus));
        }

        if (request.AgreedPickupDate < timeProvider.Today().AddDays(-RequestPickupDaysBack))
        {
            return AppError.Invalid("Purchase request", $"Its pickup date is more than {RequestPickupDaysBack} days ago, so it is no longer offered.");
        }

        return request;
    }

    /// <summary>The products of the lines, tracked and In Use (GBR-01); a product in <paramref name="alreadyOnInvoice"/> is kept even if it was discontinued since (SALE-07).</summary>
    internal async Task<Result<IReadOnlyDictionary<Guid, Product>>> ReadProductsAsync(
        IReadOnlyList<SalesInvoiceLineEntries> lines, IReadOnlySet<Guid> alreadyOnInvoice, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, Product>();
        foreach (var line in lines)
        {
            if (!found.TryGetValue(line.ProductId, out var product))
            {
                product = await products.GetForEditAsync(line.ProductId, cancellationToken);
                if (product is null)
                {
                    return AppError.Invalid($"Line {line.LineNumber} product");
                }

                found[product.Id] = product;
            }

            if (product.StatusId != StatusIds.Usage.InUse && !alreadyOnInvoice.Contains(product.Id))
            {
                return AppError.NotUsableChoice($"Product {product.Name}", statusCatalog.DisplayNameOf(product.StatusId), $"Line {line.LineNumber} product");
            }
        }

        return Result<IReadOnlyDictionary<Guid, Product>>.Success(found);
    }

    private async Task<Result<IReadOnlyList<HarvestLot>>> ReadLotsAsync(SalesInvoiceEntries entries, PermittedZones permitted, CancellationToken cancellationToken)
    {
        var byId = (await lots.ListForInvoiceAsync(entries.LotIds, cancellationToken)).ToDictionary(l => l.Id);
        var ordered = new List<HarvestLot>();
        foreach (var lotId in entries.LotIds)
        {
            if (!byId.TryGetValue(lotId, out var lot))
            {
                return AppError.Invalid($"Line {entries.Lines.First(l => l.LotId == lotId).LineNumber} harvest lot");
            }

            if (!permitted.Allows(lot.Raft.ZoneId))
            {
                return AppError.Forbidden;
            }

            // A lot split per product sells only those products: the stock of the others on it would go down with nothing harvested. A lot recorded before products has no lines and stays sellable.
            if (lot.ProductLines.Count > 0 && entries.Lines.FirstOrDefault(l => l.LotId == lotId && lot.ProductLines.All(p => p.ProductId != l.ProductId)) is { } stranger)
            {
                return AppError.Invalid($"Line {stranger.LineNumber} product", "The product is not one of the products of the harvest lot.");
            }

            ordered.Add(lot);
        }

        return Result<IReadOnlyList<HarvestLot>>.Success(ordered);
    }
}
