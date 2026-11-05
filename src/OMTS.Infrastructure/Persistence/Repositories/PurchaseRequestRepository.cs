using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PurchaseRequestRepository(OmtsDbContext db) : IPurchaseRequestRepository
{
    public void Add(PurchaseRequest request) => db.PurchaseRequests.Add(request);

    public Task<PurchaseRequest?> GetForEditAsync(Guid requestId, CancellationToken cancellationToken) =>
        db.PurchaseRequests.Include(r => r.Customer).Include(r => r.Product).FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PurchaseRequests.AsNoTracking().Where(r => r.CustomerId == customerId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(r => r.Product)
            .OrderByDescending(r => r.SentAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<PurchaseRequest> Items, int Total)> SearchAsync(int? statusId, Guid? customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.PurchaseRequests.AsNoTracking();
        if (statusId is not null)
        {
            query = query.Where(r => r.StatusId == statusId);
        }

        if (customerId is not null)
        {
            query = query.Where(r => r.CustomerId == customerId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(r => r.Customer).Include(r => r.Product)
            .OrderBy(r => r.StatusId == StatusIds.PurchaseRequest.AwaitingResponse ? 0 : 1).ThenByDescending(r => r.SentAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<PurchaseRequestSending>> ListSendingsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.PurchaseRequest && n.RelatedRecordId != null && requestIds.Contains(n.RelatedRecordId.Value)
                && n.Channel != NotificationChannel.InApp)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new PurchaseRequestSending(n.RelatedRecordId!.Value, n.Channel, n.PhoneNumber, n.DeliveryStatusId, n.DeliveryStatusAt))
            .ToListAsync(cancellationToken);
}
