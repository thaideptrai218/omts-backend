using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// The reads: SCR-SALE-02 for the Owner and the Zone Managers, who see every request since requests have no zone (PM 03/11),
/// SALE-04.1 through the customer filter, and My purchases of a trader, who sees only their own profile (SALE-10, §I.4.4 †).
/// </summary>
public sealed class PurchaseRequestReadService(
    IPurchaseRequestRepository requests,
    ICustomerRepository customers,
    PurchaseRequestViews views,
    PurchaseHarvestReader harvest,
    ZoneAccessGuard access)
{
    private const int MaxPageSize = 100;

    public async Task<Paged<PurchaseRequestView>> SearchAsync(int? statusId, Guid? customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = Clamp(page, pageSize);
        var (items, total) = await requests.SearchAsync(statusId, customerId, page, pageSize, cancellationToken);
        return new Paged<PurchaseRequestView>(await views.ForStaffAsync(items, cancellationToken), page, pageSize, total);
    }

    /// <summary>The detail of an Accepted request also holds its harvest, and what the Owner may do with it (decision of 11/11/2026).</summary>
    public async Task<Result<PurchaseRequestView>> GetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (await requests.GetAsync(requestId, cancellationToken) is not { } request)
        {
            return AppError.NotFound;
        }

        var view = (await views.ForStaffAsync([request], cancellationToken))[0];
        return request.StatusId == StatusIds.PurchaseRequest.Accepted
            ? view with { Harvest = await harvest.ReadAsync(request, await access.IsCooperativeOwnerAsync(cancellationToken), cancellationToken) }
            : view;
    }

    /// <summary>SALE-10.0: newest first; MSG-GEN-11 when the profile has no request at all (SALE-10.0.E1).</summary>
    public async Task<MyPurchases> MyPurchasesAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = Clamp(page, pageSize);
        var customer = await customers.GetByAccountIdAsync(accountId, cancellationToken);
        var (items, total) = customer is null ? ([], 0) : await requests.ListOfCustomerAsync(customer.Id, page, pageSize, cancellationToken);
        var nothing = total == 0 ? UserMessages.NothingToShowYet : null;
        return new MyPurchases(await views.ForTraderAsync(items, cancellationToken), page, pageSize, total, nothing?.Text, nothing?.Code);
    }

    /// <summary>SALE-10.1 and SALE-10.2: a request of another trader answers as not found.</summary>
    public async Task<Result<TraderPurchaseRequestView>> GetMineAsync(Guid accountId, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        return request is null || request.Customer.AccountId != accountId
            ? AppError.NotFound
            : (await views.ForTraderAsync([request], cancellationToken))[0];
    }

    // The upper bound keeps (page - 1) * pageSize inside int.
    private static (int Page, int PageSize) Clamp(int page, int pageSize) => (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
}
