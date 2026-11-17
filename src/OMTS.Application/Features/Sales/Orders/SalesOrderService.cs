using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// The orders of goods (Đặt hàng, DH) that every sales invoice is made from. The Owner takes any order; a Zone Manager takes the orders of buyers at the port for its zones. An order is saved as a Draft with its lines,
/// its discount and optionally the advance the customer pays (one save, so an order never exists without its advance); it moves along with its invoices (<see cref="SalesOrderProgress"/>) and is finished or cancelled here.
/// An order made from a trader's purchase request (Owner only) turns the request into Ordered in the same save and tells the trader. The advance is reserved for the order while it is open (<see cref="Debts.CustomerDebtAllocator"/>).
/// An order has no zone (the harvest plan decides which zones harvest for it; an invoice made from it takes the zone of its first lot), so the rights are the same for every order.
/// </summary>
public sealed class SalesOrderService(
    ISalesOrderRepository orders,
    ICustomerRepository customers,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    IProductRepository products,
    IAccountRepository accounts,
    ICustomerPaymentRepository paymentRows,
    SalesOrderAccess access,
    SalesOrderCodeService codes,
    SalesOrderViews views,
    SalesOrderProgress progress,
    CustomerPaymentService payments,
    PurchaseRequestNotifications requestNotifications,
    TraderMessageService traderMessages,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string RecordType = nameof(SalesOrder);

    // ---- Creating ----

    public async Task<Result<SalesOrderSaved>> CreateAsync(SaveSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(request.DeduplicationKey) ? null : request.DeduplicationKey.Trim();
        if (key?.Length > SalesOrderRules.MaxKeyLength)
        {
            return AppError.Invalid("Deduplication key");
        }

        if (key is not null && await orders.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first)
        {
            return await SavedAsync(first.Id, null, cancellationToken);
        }

        var (entries, invalid) = SalesOrderEntryReader.Read(request, isUpdate: false);
        if (entries is null)
        {
            return AppError.InvalidFields(invalid);
        }

        if (!await access.MayAsync(PermissionAction.Create, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var owner = await access.IsOwnerAsync(cancellationToken);
        // Only the Owner turns a trader's request into an order.
        if (entries.PurchaseRequestId is not null && !owner)
        {
            return AppError.Forbidden;
        }

        var prepared = await PrepareAsync(entries, stored: null, owner, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var plan = prepared.Value!;
        if (entries.Advance is { } wanted && wanted.Amount > plan.Total)
        {
            return AppError.Invalid("Advance amount", $"The advance cannot be more than the total of the order, {plan.Total:0} VND.");
        }

        // A typed code is checked before anything is spent; an empty one takes its number after the advance is valid.
        if (entries.Code is { } typed && await codes.DuplicateAsync(typed, null, cancellationToken) is { } taken)
        {
            return taken;
        }

        var me = currentUser.AccountId!.Value;
        var orderedAt = plan.OrderedAt;
        var order = new SalesOrder
        {
            OrderedAt = orderedAt,
            OrderDate = BusinessClock.ToBusinessDate(orderedAt),
            CustomerId = plan.Customer?.Id,
            Customer = plan.Customer,
            PurchaseRequestId = plan.Request?.Id,
            Source = entries.Source!.Value,
            ReceivedByAccountId = plan.ReceivedBy,
            CreatedByAccountId = me,
            DeduplicationKey = key,
        };
        Apply(order, entries, plan);

        CustomerPayment? advance = null;
        if (entries.Advance is { } money)
        {
            var staged = await payments.StageOrderAdvanceAsync(order, AdvanceRequest(order, money), waitsForTransfer: money.Method == SalesOrderAdvanceMethod.VietQr, cancellationToken);
            if (!staged.IsSuccess)
            {
                return staged.Error!;
            }

            advance = staged.Value!;
        }

        var (code, codeError) = await codes.ForNewAsync(entries.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        order.Code = code!;
        orders.Add(order);
        foreach (var line in order.Lines)
        {
            orders.AddLine(line);
        }

        if (plan.Customer is not null)
        {
            // A merge of the customer profile at this moment conflicts with this save.
            unitOfWork.MarkChanged(plan.Customer);
        }

        audit.Record(new AuditEntry("SalesOrder.Created", RecordType) { RecordId = order.Id, RecordCode = order.Code, After = Snapshot(order, advance) });
        Notification? sending = null;
        if (plan.Request is { } sourceRequest)
        {
            var from = sourceRequest.StatusId;
            sourceRequest.StatusId = StatusIds.PurchaseRequest.Ordered;
            unitOfWork.MarkChanged(sourceRequest);
            audit.RecordStatusChange(nameof(PurchaseRequest), sourceRequest.Id, null, from, StatusIds.PurchaseRequest.Ordered);
            sending = await requestNotifications.OrderedAsync(sourceRequest, order, cancellationToken);
        }

        var outcome = await SaveAsync(order, plan.Request, key, cancellationToken);
        if (outcome.Error is not null)
        {
            return outcome.Error;
        }

        if (outcome.ExistingOrderId is { } existing)
        {
            return await SavedAsync(existing, null, cancellationToken);
        }

        if (sending is not null)
        {
            await traderMessages.DeliverAsync(sending);
        }

        return await SavedAsync(order.Id, advance is { StatusId: StatusIds.CustomerPayment.PendingTransfer } ? advance.Id : null, cancellationToken);
    }

    // ---- Changing ----

    /// <summary>Replaces the header and the lines of an order that is Draft or Delivering, and takes the advance of the request as a new payment in the same save. The lines are matched by product and unit, so a line keeps its id (and what was delivered on it) across edits.</summary>
    public async Task<Result<SalesOrderSaved>> UpdateAsync(Guid orderId, SaveSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!SalesOrderRules.AllowsEdit(order.StatusId))
        {
            return WrongStatus(order);
        }

        if (!await access.MayAsync(PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (await rowVersions.CheckAsync(order, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (entries, invalid) = SalesOrderEntryReader.Read(request, isUpdate: true);
        if (entries is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var owner = await access.IsOwnerAsync(cancellationToken);
        var advances = await orders.AdvancesOfAsync(order.Id, cancellationToken);
        var hasLiveInvoice = await orders.HasLiveInvoiceAsync(order.Id, cancellationToken);
        // The customer owns the advances and the invoices of the order: it does not move once they exist.
        if (entries.CustomerId != order.CustomerId && (advances.Count > 0 || hasLiveInvoice))
        {
            return AppError.Invalid("Customer", "The order has advances or invoices, so its customer cannot change.");
        }

        var prepared = await PrepareAsync(entries, order, owner, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var plan = prepared.Value!;
        var paid = advances.Where(a => a.Kind == CustomerPaymentKind.Payment && a.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(a => a.Amount);
        if (paid > plan.Total)
        {
            return AppError.Invalid("Discount", $"The advances already take {paid:0} VND, more than the new total of the order, {plan.Total:0} VND.");
        }

        // A new advance taken with the save: the same limit as the advance endpoint, against the new total.
        if (entries.Advance is { } added && added.Amount > plan.Total - paid)
        {
            return AppError.Invalid("Advance amount", $"The order is short of {Math.Max(0m, plan.Total - paid):0} VND: the advance cannot be more.");
        }

        var delivered = await progress.DeliveredByLineAsync([order.Id], cancellationToken);
        if (BelowDelivered(order, plan, delivered) is { } below)
        {
            return below;
        }

        var (code, codeError) = await codes.ForEditAsync(entries.Code, order.Id, order.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var before = Snapshot(order, null);
        order.Code = code;
        order.CustomerId = plan.Customer?.Id;
        order.Customer = plan.Customer;
        order.Source = order.PurchaseRequestId is not null ? SalesOrderSource.App : entries.Source!.Value;
        order.ReceivedByAccountId = plan.ReceivedBy;
        order.OrderedAt = plan.OrderedAt;
        order.OrderDate = BusinessClock.ToBusinessDate(plan.OrderedAt);
        Apply(order, entries, plan, replaceLines: true);
        unitOfWork.MarkChanged(order);
        if (plan.Customer is not null)
        {
            unitOfWork.MarkChanged(plan.Customer);
        }

        CustomerPayment? advance = null;
        if (entries.Advance is { } money)
        {
            var staged = await payments.StageOrderAdvanceAsync(order, AdvanceRequest(order, money), waitsForTransfer: money.Method == SalesOrderAdvanceMethod.VietQr, cancellationToken);
            if (!staged.IsSuccess)
            {
                return staged.Error!;
            }

            advance = staged.Value!;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(order, advance));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("SalesOrder.Updated", RecordType) { RecordId = order.Id, RecordCode = order.Code, Before = changedBefore, After = changedAfter });
        }

        var outcome = await SaveAsync(order, null, key: null, cancellationToken);
        if (outcome.Error is not null)
        {
            return outcome.Error;
        }

        return await SavedAsync(order.Id, advance is { StatusId: StatusIds.CustomerPayment.PendingTransfer } ? advance.Id : null, cancellationToken);
    }

    /// <summary>"Kết thúc": a Delivering order is Completed by hand; what is left of its advance becomes general credit of the customer.</summary>
    public async Task<Result<SalesOrderClosed>> FinishAsync(Guid orderId, FinishSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayAsync(PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!SalesOrderRules.AllowsFinish(order.StatusId))
        {
            return WrongStatus(order);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(order, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var from = order.StatusId;
        order.StatusId = StatusIds.SalesOrder.Completed;
        order.CompletedAt = timeProvider.GetUtcNow();
        order.CompletedByAccountId = currentUser.AccountId;
        order.CompletedManually = true;
        unitOfWork.MarkChanged(order);
        audit.Record(new AuditEntry("SalesOrder.Finished", RecordType)
        {
            RecordId = order.Id,
            RecordCode = order.Code,
            Before = new { Status = statusCatalog.CodeOf(from) },
            After = new { Status = statusCatalog.CodeOf(order.StatusId), order.CompletedManually },
        });
        return await ClosedAsync(order, cancellationToken);
    }

    /// <summary>
    /// Cancels an order that is Draft or Delivering, with a reason. An order with an invoice that is not cancelled cannot be cancelled (MSG-ORDER-01): it is finished instead. The advance is left as it is: it was
    /// reserved for the order and, with the order cancelled, is general credit of the customer.
    /// </summary>
    public async Task<Result<SalesOrderClosed>> CancelAsync(Guid orderId, CancelSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayAsync(PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!SalesOrderRules.AllowsCancel(order.StatusId))
        {
            return WrongStatus(order);
        }

        if (SalesOrderRules.BlocksCancel(await orders.HasLiveInvoiceAsync(order.Id, cancellationToken)))
        {
            return AppError.Conflict(UserMessages.OrderHasInvoices(order.Code), new Dictionary<string, object?> { ["orderId"] = order.Id });
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > SalesOrderRules.MaxNoteLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(order, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var from = order.StatusId;
        order.StatusId = StatusIds.SalesOrder.Cancelled;
        order.CancelledAt = timeProvider.GetUtcNow();
        order.CancelledByAccountId = currentUser.AccountId;
        order.CancelReason = reason;
        unitOfWork.MarkChanged(order);
        audit.Record(new AuditEntry("SalesOrder.Cancelled", RecordType)
        {
            RecordId = order.Id,
            RecordCode = order.Code,
            Before = new { Status = statusCatalog.CodeOf(from) },
            After = new { Status = statusCatalog.CodeOf(order.StatusId) },
            Reason = reason,
        });
        return await ClosedAsync(order, cancellationToken);
    }

    private async Task<Result<SalesOrderClosed>> ClosedAsync(SalesOrder order, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var view = await views.ViewAsync(await orders.GetForEditAsync(order.Id, cancellationToken) ?? order, cancellationToken);
        // The order is over: what is left of its advance is general credit now.
        return new SalesOrderClosed(view, view.AdvanceLeft);
    }

    // ---- Preparing a save ----

    private sealed record PreparedLine(SalesOrderLineEntries Entry, Product Product, ProductUnit Unit, decimal UnitPrice, decimal ListPrice, decimal Amount);

    private sealed record Prepared(
        Customer? Customer, PurchaseRequest? Request, Guid ReceivedBy, DateTimeOffset OrderedAt, IReadOnlyList<PreparedLine> Lines, decimal Subtotal, decimal Total);

    /// <summary>
    /// Checks the entries against the catalogue and the database and returns what they name. MSG-GEN-01 names every invalid field; a request that cannot be ordered answers MSG-GEN-09 or MSG-ORDER-02; a price or a discount
    /// the Owner has not decided is a 409 question, answered by the flag named in <c>confirmationField</c>.
    /// </summary>
    private async Task<Result<Prepared>> PrepareAsync(SalesOrderEntries entries, SalesOrder? stored, bool owner, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var now = timeProvider.GetUtcNow();
        var orderedAt = entries.OrderedAt?.ToOffset(BusinessClock.UtcOffset) ?? stored?.OrderedAt ?? now.ToOffset(BusinessClock.UtcOffset);
        if (entries.OrderedAt is { } at && at > now + PurchaseReceiptValidator.ClockTolerance)
        {
            invalid.Add("Ordered at");
        }

        if (!owner && entries.Source != SalesOrderSource.AtPort && entries.Source != stored?.Source)
        {
            // A Zone Manager takes the buyers at the port; it may leave the source of an order it did not take as it is.
            invalid.Add("Source");
        }

        Customer? customer = null;
        if (entries.CustomerId is { } customerId)
        {
            customer = await customers.GetForEditAsync(customerId, cancellationToken);
            if (customer is null)
            {
                invalid.Add("Customer");
            }
            else if (customer.MergedIntoCustomerId is not null)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"{CustomerService.ObjectName} {customer.Name}", CustomerService.MergedStatus));
            }
            else if (customer.StatusId != StatusIds.Usage.InUse && customer.Id != stored?.CustomerId)
            {
                return AppError.NotUsableChoice($"{CustomerService.ObjectName} {customer.Name}", statusCatalog.DisplayNameOf(customer.StatusId), "Customer");
            }
        }

        var receivedBy = entries.ReceivedById ?? currentUser.AccountId!.Value;
        if (entries.ReceivedById is { } named && !(await accounts.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == named))
        {
            invalid.Add("Received by");
        }

        var catalogue = (await products.GetByIdsAsync(entries.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var storedProducts = stored?.Lines.Select(l => l.ProductId).ToHashSet() ?? [];
        var lines = new List<PreparedLine>();
        foreach (var entry in entries.Lines)
        {
            var field = $"Line {entry.LineNumber}";
            // A product that left use since stays on the order that already has it, so that order can still be edited.
            if (!catalogue.TryGetValue(entry.ProductId, out var product)
                || (product.IsHidden && !storedProducts.Contains(product.Id))
                || (product.StatusId != StatusIds.Usage.InUse && !storedProducts.Contains(product.Id)))
            {
                invalid.Add($"{field} product");
                continue;
            }

            var unit = product.Units.FirstOrDefault(u => u.UnitId == entry.UnitId);
            if (unit is null)
            {
                invalid.Add($"{field} unit");
                continue;
            }

            var price = entry.UnitPrice ?? unit.SalePrice;
            if (!SalesOrderRules.TryLineAmount(entry.Quantity, price, out var amount))
            {
                invalid.Add($"{field} amount");
                continue;
            }

            lines.Add(new PreparedLine(entry, product, unit, price, unit.SalePrice, amount));
        }

        var subtotal = lines.Sum(l => l.Amount);
        if (invalid.Count == 0 && !SalesOrderRules.DiscountFits(subtotal, entries.Discount))
        {
            invalid.Add("Discount");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        PurchaseRequest? request = null;
        if (entries.PurchaseRequestId is { } requestId)
        {
            var read = await ReadRequestAsync(requestId, customer, cancellationToken);
            if (!read.IsSuccess)
            {
                return read.Error!;
            }

            request = read.Value;
        }

        if (QuestionFor(entries, lines, stored, owner) is { } question)
        {
            return question;
        }

        return new Prepared(customer, request, receivedBy, orderedAt, lines, subtotal, SalesOrderRules.Total(subtotal, entries.Discount));
    }

    /// <summary>The request the order is made from: the profile's own, with no order yet, and still open to one.</summary>
    private async Task<Result<PurchaseRequest>> ReadRequestAsync(Guid requestId, Customer? customer, CancellationToken cancellationToken)
    {
        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        if (request is null || request.CustomerId != customer?.Id)
        {
            return AppError.Invalid("Purchase request");
        }

        if (await orders.FindByPurchaseRequestAsync(request.Id, cancellationToken) is { } existing)
        {
            return OrderExists(existing);
        }

        var fulfilled = request.StatusId == StatusIds.PurchaseRequest.Accepted && (await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id);
        if (!SalesOrderRules.RequestAllowsOrder(request.StatusId, fulfilled))
        {
            return PurchaseRequestRules.WrongStatus(fulfilled ? PurchaseRequestRules.FulfilledStatus : statusCatalog.DisplayNameOf(request.StatusId));
        }

        return request;
    }

    /// <summary>MSG-ORDER-02 with the order the request already made.</summary>
    internal static AppError OrderExists(SalesOrder existing) => AppError.Conflict(
        UserMessages.PurchaseRequestAlreadyOrdered(existing.Code),
        new Dictionary<string, object?> { ["field"] = "Purchase request", ["existingId"] = existing.Id, ["existingCode"] = existing.Code });

    /// <summary>MSG-SALE-10: a price other than the list price, or a discount, that the Owner has not decided. A line or a discount as it was saved is not asked again.</summary>
    private static AppError? QuestionFor(SalesOrderEntries entries, IReadOnlyList<PreparedLine> lines, SalesOrder? stored, bool owner)
    {
        foreach (var line in lines)
        {
            var unchanged = stored?.Lines.Any(l => l.ProductId == line.Product.Id && l.UnitId == line.Unit.UnitId && l.UnitPrice == line.UnitPrice) == true;
            if (!unchanged && SalesOrderRules.PriceNeedsOwner(line.UnitPrice, line.ListPrice, owner, line.Entry.PriceDecidedByOwner))
            {
                return SalesOrderRules.PriceQuestion(line.Product.Name, line.Unit.Unit.Name, line.ListPrice, line.UnitPrice, line.Entry.LineNumber);
            }
        }

        var sameDiscount = stored is { DiscountDecidedByOwner: true } && stored.DiscountAmount == entries.Discount;
        return !sameDiscount && SalesOrderRules.DiscountNeedsOwner(entries.Discount, owner, entries.DiscountDecidedByOwner) ? SalesOrderRules.DiscountQuestion(entries.Discount) : null;
    }

    /// <summary>MSG-ORDER-03: a line cannot go below what was delivered on it, nor be removed once something was delivered.</summary>
    private static AppError? BelowDelivered(SalesOrder order, Prepared plan, IReadOnlyDictionary<Guid, decimal> delivered)
    {
        foreach (var line in order.Lines)
        {
            if (!delivered.TryGetValue(line.Id, out var done) || done <= 0)
            {
                continue;
            }

            var kept = plan.Lines.Where(l => l.Product.Id == line.ProductId && l.Unit.UnitId == line.UnitId).Sum(l => l.Entry.Quantity);
            if (kept < done)
            {
                return AppError.Conflict(
                    UserMessages.OrderQuantityBelowDelivered(line.Product?.Name ?? line.UnitCode, line.UnitName, done),
                    new Dictionary<string, object?> { ["lineId"] = line.Id, ["deliveredQuantity"] = done });
            }
        }

        return null;
    }

    // ---- Applying a save ----

    /// <summary>Sets the header and the lines of the order from the prepared entries; <paramref name="replaceLines"/> matches them with the lines it has, by product and unit.</summary>
    private void Apply(SalesOrder order, SalesOrderEntries entries, Prepared plan, bool replaceLines = false)
    {
        order.PickupAt = entries.PickupAt?.ToOffset(BusinessClock.UtcOffset);
        order.PickupDate = order.PickupAt is { } pickup ? BusinessClock.ToBusinessDate(pickup) : null;
        order.DiscountAmount = entries.Discount;
        order.DiscountDecidedByOwner = entries.DiscountDecidedByOwner;
        order.Subtotal = plan.Subtotal;
        order.Total = plan.Total;
        order.Note = entries.Note;

        var unused = replaceLines ? order.Lines.ToList() : [];
        var number = 0;
        foreach (var prepared in plan.Lines)
        {
            number++;
            var line = unused.FirstOrDefault(l => l.ProductId == prepared.Product.Id && l.UnitId == prepared.Unit.UnitId);
            var isNew = line is null;
            if (line is not null)
            {
                unused.Remove(line);
            }

            // A line kept as it was keeps the list price it was saved with; a new one, or one with another price, takes the price of the unit now.
            var takesListPrice = line is null || line.UnitPrice != prepared.UnitPrice;

            line ??= new SalesOrderLine { OrderId = order.Id, ProductId = prepared.Product.Id, UnitId = prepared.Unit.UnitId };
            line.LineNumber = number;
            line.UnitCode = prepared.Unit.Code;
            line.UnitName = prepared.Unit.Unit.Name;
            line.Factor = prepared.Unit.Factor;
            line.Quantity = prepared.Entry.Quantity;
            line.UnitPrice = prepared.UnitPrice;
            line.ListPrice = takesListPrice ? prepared.ListPrice : line.ListPrice;
            line.Amount = prepared.Amount;
            line.Note = prepared.Entry.Note;
            if (isNew)
            {
                order.Lines.Add(line);
                if (replaceLines)
                {
                    // A new line under a tracked order has its key set: it is added explicitly, or it would be taken for an existing row.
                    orders.AddLine(line);
                }
            }
        }

        foreach (var removed in unused)
        {
            order.Lines.Remove(removed);
            orders.RemoveLine(removed);
        }
    }

    private RecordCustomerPaymentRequest AdvanceRequest(SalesOrder order, SalesOrderAdvanceEntries money) => new(
        order.CustomerId,
        PaymentDate: null,
        money.Amount,
        money.Method == SalesOrderAdvanceMethod.Cash ? CustomerPaymentMethod.Cash : CustomerPaymentMethod.OtherTransfer,
        money.BankReference,
        money.CollectedById,
        money.Note,
        ConfirmDuplicateBankReference: money.ConfirmDuplicateBankReference,
        PaidAt: money.PaidAt ?? timeProvider.GetUtcNow(),
        CashAccountId: money.CashAccountId);

    private sealed record SaveOutcome(AppError? Error, Guid? ExistingOrderId);

    /// <summary>
    /// The one save of an order with its advance and its request. The loser of two parallel saves with one code answers MSG-GEN-02; one with the same deduplication key returns the order of the winner; a request
    /// ordered or cancelled meanwhile answers MSG-ORDER-02 or MSG-GEN-09.
    /// </summary>
    private async Task<SaveOutcome> SaveAsync(SalesOrder order, PurchaseRequest? request, string? key, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new SaveOutcome(null, null);
        }
        catch (DuplicateRecordException)
        {
            unitOfWork.DiscardChanges();
            if (key is not null && await orders.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first)
            {
                return new SaveOutcome(null, first.Id);
            }

            if (await codes.DuplicateAsync(order.Code, order.Id, cancellationToken) is { } duplicate)
            {
                return new SaveOutcome(duplicate, null);
            }

            if (request is not null && await orders.FindByPurchaseRequestAsync(request.Id, cancellationToken) is { } existing)
            {
                return new SaveOutcome(OrderExists(existing), null);
            }

            return new SaveOutcome(AppError.Conflict(UserMessages.AlreadyExists(SalesOrderCodeService.ObjectName)), null);
        }
        catch (ConcurrencyConflictException) when (request is not null)
        {
            await unitOfWork.ReloadAsync(request, cancellationToken);
            if (SalesOrderRules.RequestAllowsOrder(request.StatusId, fulfilled: false))
            {
                throw;
            }

            return new SaveOutcome(
                request.StatusId == StatusIds.PurchaseRequest.Ordered && await orders.FindByPurchaseRequestAsync(request.Id, cancellationToken) is { } ordered
                    ? OrderExists(ordered)
                    : PurchaseRequestRules.WrongStatus(statusCatalog.DisplayNameOf(request.StatusId)),
                null);
        }
    }

    private async Task<Result<SalesOrderSaved>> SavedAsync(Guid orderId, Guid? qrPaymentId, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null || !await access.MayAsync(PermissionAction.View, cancellationToken))
        {
            return AppError.NotFound;
        }

        CustomerPaymentQr? qr = null;
        if (qrPaymentId is { } paymentId && await paymentRows.GetAsync(paymentId, cancellationToken) is { } payment)
        {
            qr = CustomerPaymentService.QrOf(payment);
        }

        var saved = UserMessages.Saved($"Sales order {order.Code}");
        return new SalesOrderSaved(await views.ViewAsync(order, cancellationToken), saved.Text, saved.Code, qr);
    }

    private static object Snapshot(SalesOrder o, CustomerPayment? advance) => new
    {
        o.Code, o.OrderedAt, o.PickupAt, o.CustomerId, o.PurchaseRequestId, o.Source, o.ReceivedByAccountId, o.DiscountAmount, o.DiscountDecidedByOwner, o.Subtotal, o.Total, o.Note,
        Lines = o.Lines.OrderBy(l => l.LineNumber).Select(l => new { l.LineNumber, l.ProductId, l.UnitId, l.UnitCode, l.Quantity, l.UnitPrice, l.ListPrice, l.Amount, l.Note }).ToList(),
        AdvanceAmount = advance?.Amount,
        AdvanceMethod = advance?.Method,
    };

    private AppError WrongStatus(SalesOrder order) =>
        AppError.Conflict(UserMessages.WrongStatus($"{SalesOrderCodeService.ObjectName} {order.Code}", statusCatalog.DisplayNameOf(order.StatusId)));
}
