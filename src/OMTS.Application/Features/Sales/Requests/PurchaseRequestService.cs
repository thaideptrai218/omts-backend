using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using S = OMTS.Domain.Constants.StatusIds.PurchaseRequest;

namespace OMTS.Application.Features.Sales.Requests;

/// <summary>SALE-01/02/03 with whole-cage quantities per the user's 31/10/2026 correction.
/// No price, stock reservation, weight conversion, invoice or payment is created by a request.</summary>
public sealed class PurchaseRequestService(
    IPurchaseRequestRepository requests, ISalesProductRepository products, ICustomerRepository customers,
    IAccountRepository accounts, ICurrentUser user, PermissionService permissions,
    RowVersionGuard versions, IStatusCatalog statuses, AuditLogService audit,
    INotificationDispatcher notifications, IUnitOfWork uow, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);

    public async Task<Result<PurchaseRequestPage>> ListAsync(bool mine, string? status, int page, int pageSize, CancellationToken ct)
    {
        var who = await AccessAsync(ct);
        Guid? customerId = null;
        if (mine)
        {
            if (who?.SubjectType != SubjectType.Trader) return AppError.Forbidden;
            var customer = await customers.GetByAccountIdAsync(who.AccountId, ct);
            if (customer is null || customer.MergedIntoCustomerId is not null) return AppError.Invalid("Customer profile");
            customerId = customer.Id;
        }
        else if (who is null || !who.Allows(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone, null))
            return AppError.Forbidden;
        if (page < 1 || pageSize is < 1 or > 100 || page > int.MaxValue / pageSize) return AppError.Invalid("Page");
        int? statusId = status switch
        {
            null or "" or "ALL" => null, "AWAITING_RESPONSE" => S.AwaitingResponse,
            "ACCEPTED" => S.Accepted, "DECLINED" => S.Declined, "CANCELLED" => S.Cancelled, _ => -1,
        };
        if (statusId == -1) return AppError.Invalid("Status");
        var found = await requests.SearchAsync(customerId, statusId, page, pageSize, ct);
        return new PurchaseRequestPage(found.Items.Select(View).ToList(), page, pageSize, found.Total);
    }

    public async Task<Result<PurchaseRequestView>> GetAsync(Guid id, bool mine, CancellationToken ct)
    {
        var who = await AccessAsync(ct);
        if (who is null) return AppError.Forbidden;
        var row = await requests.GetAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (mine)
        {
            if (who.SubjectType != SubjectType.Trader || row.Customer.AccountId != who.AccountId) return AppError.Forbidden;
        }
        else if (!who.Allows(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone, null)) return AppError.Forbidden;
        return View(row);
    }

    public async Task<Result<SubmittedPurchaseRequest>> SubmitAsync(CreatePurchaseRequest input, CancellationToken ct)
    {
        var who = await AccessAsync(ct);
        if (who?.SubjectType != SubjectType.Trader) return AppError.Forbidden;
        if (input.RequestedCages <= 0) return AppError.Invalid("Requested cages");
        if (input.Note?.Length > 500) return AppError.Invalid("Note");
        if (input.ClientOperationId == Guid.Empty) return AppError.Invalid("Client operation");
        var customer = await customers.GetByAccountIdAsync(who.AccountId, ct);
        if (customer is not { StatusId: StatusIds.Usage.InUse } || customer.MergedIntoCustomerId is not null)
            return AppError.Invalid("Customer profile");
        var note = input.Note?.Trim();
        if (await requests.FindOperationAsync(customer.Id, input.ClientOperationId, ct) is { } replay)
        {
            if (replay.ProductId != input.ProductId || replay.RequestedCages != input.RequestedCages ||
                replay.PickupDate != input.PickupDate || replay.Note != note) return AppError.Invalid("Client operation");
            return new SubmittedPurchaseRequest(View(replay), true);
        }
        if (input.PickupDate == default || input.PickupDate < Today) return AppError.Invalid("Pickup date");
        var product = await products.GetAsync(input.ProductId, ct);
        if (product is null) return AppError.Invalid("Product");
        if (!SalesProductService.Available(product, Today)) return AppError.Invalid("Product unavailable");
        var row = new PurchaseRequest
        {
            CustomerId = customer.Id, Customer = customer, ProductId = product.Id, ProductName = product.Name,
            RequestedCages = input.RequestedCages, PickupDate = input.PickupDate,
            Note = note, ClientOperationId = input.ClientOperationId,
        };
        row.Code = "YC-" + Today.ToString("yyyyMMdd") + "-" + row.Id.ToString("N")[..12].ToUpperInvariant();
        requests.Add(row);
        audit.Record(new AuditEntry("PurchaseRequest.Submitted", nameof(PurchaseRequest))
        { RecordId = row.Id, RecordCode = row.Code, After = new { row.ProductId, row.RequestedCages, row.PickupDate, row.Note } });
        await notifications.NotifyAsync(await accounts.ListActiveOwnerIdsAsync(ct), NotificationTypes.NewOrder,
            "Yêu cầu mua mới", $"{customer.Name} đặt {row.RequestedCages} lồng {row.ProductName}, nhận {row.PickupDate:dd/MM/yyyy}.",
            nameof(PurchaseRequest), row.Code, true, ct);
        await uow.SaveChangesAsync(ct);
        return new SubmittedPurchaseRequest(View(row), false);
    }

    public async Task<Result<PurchaseRequestView>> RespondAsync(Guid id, RespondPurchaseRequest input, CancellationToken ct)
    {
        var who = await AccessAsync(ct);
        if (who?.IsCooperativeOwner != true) return AppError.Forbidden;
        var row = await requests.GetAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (row.StatusId != S.AwaitingResponse) return AppError.AlreadyExists("Answered purchase request", row.Id);
        if (await versions.CheckAsync(row, input.RowVersion, ct) is { } stale) return stale;
        if (input.Note?.Length > 500 || (!input.Accept && string.IsNullOrWhiteSpace(input.Note))) return AppError.Invalid("Response");
        if (input.Accept && (input.AcceptedCages is null or <= 0 || input.AcceptedCages > row.RequestedCages))
            return AppError.Invalid("Accepted cages");
        if (input.Accept && (input.PickupDate is null || input.PickupDate < Today)) return AppError.Invalid("Pickup date");
        row.StatusId = input.Accept ? S.Accepted : S.Declined;
        row.AcceptedCages = input.Accept ? input.AcceptedCages : null;
        row.AcceptedPickupDate = input.Accept ? input.PickupDate : null;
        row.AllowPayLater = input.Accept && input.AllowPayLater;
        row.ResponseNote = input.Note?.Trim(); row.RespondedAt = clock.GetUtcNow(); row.RespondedByAccountId = who.AccountId;
        audit.Record(new AuditEntry("PurchaseRequest.Responded", nameof(PurchaseRequest))
        { RecordId = row.Id, RecordCode = row.Code, Before = new { StatusId = S.AwaitingResponse },
            After = new { row.StatusId, row.AcceptedCages, row.AcceptedPickupDate, row.AllowPayLater, row.ResponseNote } });
        if (row.Customer.AccountId is { } trader)
            await notifications.NotifyAsync([trader], "PURCHASE_REQUEST_RESPONDED", "HTX phản hồi yêu cầu mua",
                input.Accept ? $"{row.Code}: chấp nhận {row.AcceptedCages} lồng, nhận {row.AcceptedPickupDate:dd/MM/yyyy}."
                    : $"{row.Code}: từ chối. {row.ResponseNote}", nameof(PurchaseRequest), row.Code, true, ct);
        await uow.SaveChangesAsync(ct);
        return View(row);
    }

    public async Task<Result<PurchaseRequestView>> CancelAsync(Guid id, CancelPurchaseRequest input, CancellationToken ct)
    {
        var who = await AccessAsync(ct);
        if (who?.SubjectType != SubjectType.Trader) return AppError.Forbidden;
        var row = await requests.GetAsync(id, ct);
        if (row is null) return AppError.NotFound;
        if (row.Customer.AccountId != who.AccountId) return AppError.Forbidden;
        if (row.StatusId == S.Cancelled) return View(row);
        if (row.StatusId is not (S.AwaitingResponse or S.Accepted)) return AppError.Invalid("Purchase request status");
        if (await versions.CheckAsync(row, input.RowVersion, ct) is { } stale) return stale;
        if (input.Reason?.Length > 500) return AppError.Invalid("Reason");
        var before = row.StatusId;
        row.StatusId = S.Cancelled; row.CancelledAt = clock.GetUtcNow(); row.CancellationReason = input.Reason?.Trim();
        audit.Record(new AuditEntry("PurchaseRequest.Cancelled", nameof(PurchaseRequest))
        { RecordId = row.Id, RecordCode = row.Code, Before = new { StatusId = before }, After = new { row.StatusId }, Reason = row.CancellationReason });
        await notifications.NotifyAsync(await accounts.ListActiveOwnerIdsAsync(ct), "PURCHASE_REQUEST_CANCELLED",
            "Thương lái hủy yêu cầu mua", $"{row.Customer.Name} đã hủy {row.Code}.", nameof(PurchaseRequest), row.Code, false, ct);
        await uow.SaveChangesAsync(ct);
        return View(row);
    }

    private Task<AccountAccess?> AccessAsync(CancellationToken ct) => user.AccountId is { } id
        ? permissions.GetAccessAsync(id, ct) : Task.FromResult<AccountAccess?>(null);
    private PurchaseRequestView View(PurchaseRequest r) => new(
        r.Id, r.Code, r.ProductId, r.ProductName, "Cage", r.RequestedCages, r.PickupDate, r.Note,
        statuses.CodeOf(r.StatusId), r.AcceptedCages, r.AcceptedPickupDate, r.AllowPayLater,
        r.ResponseNote, r.RespondedAt, r.CancelledAt, r.CancellationReason, r.CreatedAt, r.Customer.Name, RowVersionGuard.Encode(r.RowVersion));
}
