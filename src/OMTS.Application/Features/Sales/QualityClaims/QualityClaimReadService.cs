using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// The reads: SCR-CLAIM-02 for the Cooperative Owner and the Zone Managers, who see the claims of invoices in their zones only (PM question), and the trader's own claims (BR-ACCOUNT-04).
/// Claims of a merged profile were re-pointed by the merge, so they count as the kept profile's own. A claim of another trader answers as not found.
/// </summary>
public sealed class QualityClaimReadService(
    IQualityClaimRepository claims,
    ICustomerRepository customers,
    QualityClaimViews views,
    ZoneAccessGuard zoneAccess)
{
    private const int MaxPageSize = 100;

    public async Task<QualityClaimsPage> SearchAsync(int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = Clamp(page, pageSize);
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.QualityClaims, PermissionAction.View, cancellationToken);
        var (items, total) = await claims.SearchAsync(zones, statusId, page, pageSize, cancellationToken);
        var nothing = total > 0 ? null : statusId is null ? UserMessages.NothingToShowYet : UserMessages.NoDataForFilters("filters");
        return new QualityClaimsPage(items.Select(c => views.Row(c, forStaff: true)).ToList(), page, pageSize, total, nothing?.Text, nothing?.Code);
    }

    /// <summary>The claim in a zone the caller may view claims of (GFR-02): another zone answers 403 MSG-GEN-08.</summary>
    public async Task<Result<QualityClaimView>> GetAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await claims.GetDetailAsync(claimId, cancellationToken);
        if (claim is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.QualityClaims, PermissionAction.View, claim.Invoice.ZoneId, cancellationToken)
            ? await views.ForStaffAsync(claim, cancellationToken)
            : AppError.Forbidden;
    }

    public async Task<QualityClaimsPage> MineAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = Clamp(page, pageSize);
        var customer = await customers.GetByAccountIdAsync(accountId, cancellationToken);
        var (items, total) = customer is null ? ([], 0) : await claims.ListOfCustomerAsync(customer.Id, page, pageSize, cancellationToken);
        var nothing = total == 0 ? UserMessages.NothingToShowYet : null;
        return new QualityClaimsPage(items.Select(c => views.Row(c, forStaff: false)).ToList(), page, pageSize, total, nothing?.Text, nothing?.Code);
    }

    /// <summary>Only the trader whose profile the claim names; any other answers as not found.</summary>
    public async Task<Result<TraderQualityClaimView>> GetMineAsync(Guid accountId, Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await claims.GetDetailAsync(claimId, cancellationToken);
        return claim is null || claim.Customer.AccountId != accountId
            ? AppError.NotFound
            : await views.ForTraderAsync(claim, cancellationToken);
    }

    /// <summary>The upper bound keeps (page - 1) * pageSize inside int.</summary>
    private static (int Page, int PageSize) Clamp(int page, int pageSize) => (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
}
