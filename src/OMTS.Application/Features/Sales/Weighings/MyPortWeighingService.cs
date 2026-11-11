using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Weighings.Dtos;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Weighings;

/// <summary>
/// The weighing at the port on the trader's side: only the weighings of the trader's own requests (§I.4.4 †), and the confirmation that closes the figures so the invoice can be made. A
/// weighing of another profile answers as not found.
/// </summary>
public sealed class MyPortWeighingService(
    IPortWeighingRepository weighings,
    ICustomerRepository customers,
    RowVersionGuard versions,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public async Task<Result<MyPortWeighingList>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        // The page number is capped so the offset cannot overflow.
        var (pageNumber, size) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (items, total) = await weighings.ListOfCustomerAsync(customer.Id, pageNumber, size, cancellationToken);
        var empty = total == 0 ? UserMessages.NothingToShowYet : null;
        return new MyPortWeighingList(new Paged<PortWeighingView>(items.Select(PortWeighingViews.Of).ToList(), pageNumber, size, total), empty?.Text, empty?.Code);
    }

    public async Task<Result<PortWeighingView>> GetAsync(Guid weighingId, CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        var weighing = await weighings.GetAsync(weighingId, cancellationToken);
        return weighing is null || weighing.PurchaseRequest.CustomerId != customer.Id ? AppError.NotFound : PortWeighingViews.Of(weighing);
    }

    /// <summary>The trader agrees to the figures read. A second tap answers the confirmed weighing and changes nothing.</summary>
    public async Task<Result<PortWeighingView>> ConfirmAsync(Guid weighingId, ConfirmPortWeighingRequest input, CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        var weighing = await weighings.GetForEditAsync(weighingId, cancellationToken);
        if (weighing is null || weighing.PurchaseRequest.CustomerId != customer.Id)
        {
            return AppError.NotFound;
        }

        if (weighing.IsConfirmed)
        {
            return PortWeighingViews.Of(weighing);
        }

        if (await versions.CheckAsync(weighing, input.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        weighing.ConfirmedAt = timeProvider.GetUtcNow();
        weighing.ConfirmedByAccountId = currentUser.AccountId!.Value;
        audit.Record(new AuditEntry("PortWeighing.Confirmed", nameof(PortWeighing))
        {
            RecordId = weighing.Id,
            RecordCode = weighing.PurchaseRequest.Code,
            After = new { weighing.ConfirmedAt, CageCount = weighing.Cages.Count, TotalKg = weighing.Cages.Sum(c => c.Kg) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return PortWeighingViews.Of(await weighings.GetAsync(weighing.Id, cancellationToken) ?? weighing);
    }

    /// <summary>The profile linked to the trader's account; a merged profile holds nothing any more.</summary>
    private async Task<Customer?> ProfileAsync(CancellationToken cancellationToken)
    {
        var customer = await customers.GetByAccountIdAsync(currentUser.AccountId!.Value, cancellationToken);
        return customer is null || customer.MergedIntoCustomerId is not null ? null : customer;
    }
}
