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
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>What the entries name, read tracked as the save will write it, and the debt of the customer before the invoice (BR-SALE-07 (4)).</summary>
/// <param name="Standing">Where the customer stands against the debt limit; null for a walk-in customer, or while the debt warning is off.</param>
/// <param name="AllowMoreDebtOverLimit">False: an invoice that leaves a debt for a customer over the limit is refused.</param>
internal sealed record SalesInvoiceFacts(
    Customer? Customer, PurchaseRequest? Request, IReadOnlyDictionary<Guid, Product> Products, IReadOnlyList<HarvestLot> Lots, Guid ZoneId, bool ByOwner, decimal DebtBefore,
    CustomerDebtStanding? Standing = null, bool AllowMoreDebtOverLimit = true);

/// <summary>
/// SALE-05 steps 2 to 6 against the database: the profile, the request (SALE-05.3), the products In Use (GBR-01), the lots for sale in zones the user can access (BR-SALE-06, BR-FARM-08)
/// and the zone of the invoice (BR-SALE-08). The lots are marked changed, so a parallel close or sale of one of them makes one of the two saves fail.
/// </summary>
public sealed class SalesInvoiceFactsReader(
    ICustomerRepository customers,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
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

        var chosenCustomer = await ReadCustomerAsync(entries.CustomerId, cancellationToken);
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

        var lineLots = await ReadLotsAsync(entries, permitted, cancellationToken);
        if (!lineLots.IsSuccess)
        {
            return lineLots.Error!;
        }

        var zoneId = entries.ZoneId ?? lineLots.Value![0].Raft.ZoneId;
        if (entries.ZoneId is { } chosen && (await zones.GetByIdsAsync([chosen], cancellationToken)).Count == 0)
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
        var debt = customer is null ? 0m : await debts.DebtAsync(customer.Id, cancellationToken);
        var policy = await standings.PolicyAsync(cancellationToken);
        var standing = customer is not null && policy.Enabled ? (await standings.ForAsync([customer.Id], policy, cancellationToken))[customer.Id] : null;
        return new SalesInvoiceFacts(customer, request.Value, lineProducts.Value!, lineLots.Value!, zoneId, byOwner, debt, standing, policy.AllowMoreOverLimit);
    }

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
