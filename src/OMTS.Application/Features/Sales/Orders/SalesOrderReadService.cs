using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>The reads of the orders: the list (and its export), one order by id or code, the code the next order would get, the form of an order made from a purchase request, and the print of an order.</summary>
public sealed class SalesOrderReadService(
    ISalesOrderRepository orders,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    IProductRepository products,
    SalesOrderAccess access,
    SalesOrderCodeService codes,
    SalesOrderViews views,
    ISpreadsheetWriter spreadsheets,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    private static readonly int[] StatusIdsOfOrders =
        [StatusIds.SalesOrder.Draft, StatusIds.SalesOrder.Delivering, StatusIds.SalesOrder.Completed, StatusIds.SalesOrder.Cancelled];

    public async Task<Result<SalesOrderPage>> SearchAsync(SalesOrderListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.View, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (rows, total, totals) = await orders.SearchAsync(filter.Value!, page, pageSize, cancellationToken);
        return new SalesOrderPage(
            await views.ListItemsAsync(rows, cancellationToken), page, pageSize, total, new SalesOrderListSummary(totals.Total, totals.Invoiced, totals.CustomerPaid));
    }

    public async Task<Result<ExportedFile>> ExportAsync(SalesOrderListQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(query, PermissionAction.Export, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var items = await views.ListItemsAsync(await orders.ListAsync(filter.Value!, cancellationToken), cancellationToken);
        string[] headers =
        [
            "Code", "Status", "Ordered at", "Pickup at", "Customer code", "Customer", "Phone", "Source", "Received by", "Created by", "Total", "Invoiced", "Customer paid",
        ];
        var rows = items.Select(i => (IReadOnlyList<object?>)new List<object?>
        {
            i.Code, i.Status, Local(i.OrderedAt), i.PickupAt is { } pickup ? Local(pickup) : null, i.CustomerCode, i.CustomerName, i.CustomerPhone, i.Source.ToString(),
            i.ReceivedBy, i.CreatedBy, i.Total, i.InvoicedTotal, i.CustomerPaid,
        }).ToList();
        return Write($"sales-orders-{timeProvider.Today():yyyyMMdd}.xlsx", [new SpreadsheetSheet("Orders", headers, rows)]);
    }

    public async Task<Result<SalesOrderView>> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        return order is null || !await access.MayAsync(PermissionAction.View, cancellationToken) ? AppError.NotFound : await views.ViewAsync(order, cancellationToken);
    }

    /// <summary>The order with this code regardless of letter case, for the link that opens it by its code.</summary>
    public async Task<Result<SalesOrderView>> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        await orders.FindByCodeAsync(code.Trim(), null, cancellationToken) is { } found ? await GetAsync(found.Id, cancellationToken) : AppError.NotFound;

    /// <summary>The code the next order would get; not reserved.</summary>
    public async Task<Result<SalesOrderNextCode>> NextCodeAsync(CancellationToken cancellationToken) =>
        await access.MayAsync(PermissionAction.Create, cancellationToken) ? new SalesOrderNextCode(await codes.PeekAsync(cancellationToken)) : AppError.Forbidden;

    /// <summary>
    /// The form of a new order made from a purchase request, for the Owner: the customer, the product in kg with the quantity the Owner accepted (the quantity asked when it was not answered yet), the pickup time
    /// (the agreed date, else the date asked, at 00:00) and the source App. 409 MSG-ORDER-02 when the request has an order; 409 MSG-GEN-09 when it cannot be ordered.
    /// </summary>
    public async Task<Result<SalesOrderRequestDraft>> RequestDraftAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!await access.IsOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        if (request is null)
        {
            return AppError.NotFound;
        }

        if (await orders.FindByPurchaseRequestAsync(request.Id, cancellationToken) is { } existing)
        {
            return SalesOrderService.OrderExists(existing);
        }

        var fulfilled = request.StatusId == StatusIds.PurchaseRequest.Accepted && (await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id);
        if (!SalesOrderRules.RequestAllowsOrder(request.StatusId, fulfilled))
        {
            return PurchaseRequestRules.WrongStatus(fulfilled ? PurchaseRequestRules.FulfilledStatus : statusCatalog.DisplayNameOf(request.StatusId));
        }

        var product = (await products.GetByIdsAsync([request.ProductId], cancellationToken)).FirstOrDefault();
        var baseUnit = product?.Units.FirstOrDefault(u => u.IsBase);
        if (product is null || baseUnit is null)
        {
            return AppError.Invalid("Product");
        }

        var quantity = request.AcceptedKg ?? request.RequestedKg;
        var pickup = BusinessClock.FromLocal((request.AgreedPickupDate ?? request.PickupDate).ToDateTime(TimeOnly.MinValue));
        return new SalesOrderRequestDraft(
            request.Id,
            new SalesOrderCustomerView(request.Customer.Id, request.Customer.Code, request.Customer.Name, request.Customer.PhoneNumber),
            request.SentAt,
            request.StatusId,
            [new SalesOrderDraftLine(product.Id, product.Code, product.Name, baseUnit.UnitId, baseUnit.Code, baseUnit.Unit.Name, quantity, baseUnit.SalePrice)],
            pickup,
            Domain.Enums.SalesOrderSource.App,
            request.Note);
    }

    /// <summary>One order as an .xlsx file: its header and its lines.</summary>
    public async Task<Result<ExportedFile>> PrintAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayAsync(PermissionAction.Export, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var view = await views.ViewAsync(order, cancellationToken);
        IReadOnlyList<object?> Row(string name, object? value) => new List<object?> { name, value };
        var header = new List<IReadOnlyList<object?>>
        {
            Row("Code", view.Code), Row("Status", view.Status), Row("Ordered at", Local(view.OrderedAt)), Row("Pickup at", view.PickupAt is { } pickup ? Local(pickup) : null),
            Row("Customer code", view.Customer?.Code), Row("Customer", view.Customer?.Name ?? "Walk-in"), Row("Phone", view.Customer?.PhoneNumber),
            Row("Source", view.Source.ToString()), Row("Received by", view.ReceivedBy.Name), Row("Note", view.Note), Row("Subtotal", view.Subtotal), Row("Discount", view.Discount),
            Row("Total", view.Total), Row("Customer paid", view.CustomerPaid),
        };
        var lines = view.Lines.Select(l => (IReadOnlyList<object?>)new List<object?>
        {
            l.LineNumber, l.ProductCode, l.ProductName, l.UnitName, l.Quantity, l.UnitPrice, l.Amount, l.Note,
        }).ToList();
        return Write($"sales-order-{view.Code}.xlsx",
        [
            new SpreadsheetSheet("Order", ["Field", "Value"], header),
            new SpreadsheetSheet("Lines", ["No.", "Product code", "Product", "Unit", "Quantity", "Unit price", "Amount", "Note"], lines),
        ]);
    }

    private async Task<Result<SalesOrderFilter>> FilterAsync(SalesOrderListQuery query, PermissionAction action, CancellationToken cancellationToken)
    {
        if (!await access.MayAsync(action, cancellationToken))
        {
            return AppError.Forbidden;
        }

        SalesOrderSort? sort = query.SortBy switch
        {
            null or "code" => SalesOrderSort.Code,
            "orderedAt" => SalesOrderSort.OrderedAt,
            "pickupAt" => SalesOrderSort.PickupAt,
            "total" => SalesOrderSort.Total,
            _ => null,
        };
        if (sort is null)
        {
            return AppError.Invalid("Sort by");
        }

        if (query.SortDir is not (null or "asc" or "desc"))
        {
            return AppError.Invalid("Sort direction");
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        if (query.PickupFrom > query.PickupTo)
        {
            return AppError.Invalid("Pickup period");
        }

        if (query.Source is { } source && !Enum.IsDefined(source))
        {
            return AppError.Invalid("Source");
        }

        if (query.StatusIds is { } statusIds && statusIds.Any(id => !StatusIdsOfOrders.Contains(id)))
        {
            return AppError.Invalid("Status");
        }

        var text = DisplayNames.Normalize(query.Search);
        return new SalesOrderFilter(
            text.Length == 0 ? null : text, query.StatusIds is { Count: > 0 } ? query.StatusIds : null, query.From, query.To, query.PickupFrom, query.PickupTo, query.CustomerId,
            query.Source, query.CreatedById, query.ReceivedById, sort.Value, query.SortDir != "asc");
    }

    private static DateTime Local(DateTimeOffset at) => at.ToOffset(BusinessClock.UtcOffset).DateTime;

    private Result<ExportedFile> Write(string fileName, IReadOnlyList<SpreadsheetSheet> sheets)
    {
        try
        {
            return new ExportedFile(fileName, ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }
}
