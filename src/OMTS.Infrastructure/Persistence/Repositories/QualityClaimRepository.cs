using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class QualityClaimRepository(OmtsDbContext db) : IQualityClaimRepository
{
    public void Add(QualityClaim claim) => db.QualityClaims.Add(claim);

    public Task<bool> HasAwaitingAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.QualityClaims.AnyAsync(c => c.InvoiceId == invoiceId && (c.StatusId == StatusIds.QualityClaim.AwaitingResponse || c.StatusId == StatusIds.QualityClaim.OfferMade), cancellationToken);

    public Task<QualityClaim?> GetForEditAsync(Guid claimId, CancellationToken cancellationToken) =>
        db.QualityClaims.Include(c => c.Customer).Include(c => c.Invoice).FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);

    public Task<QualityClaim?> GetDetailAsync(Guid claimId, CancellationToken cancellationToken) =>
        Detail().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);

    public async Task<(IReadOnlyList<QualityClaim> Items, int Total)> SearchAsync(PermittedZones zones, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.QualityClaims.AsNoTracking();
        if (!zones.AllZones)
        {
            var zoneIds = zones.ZoneIds.ToList();
            query = query.Where(c => zoneIds.Contains(c.Invoice.ZoneId));
        }

        if (statusId is not null)
        {
            query = query.Where(c => c.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(c => c.Customer).Include(c => c.Invoice).Include(c => c.Photos)
            .OrderBy(c => c.StatusId == StatusIds.QualityClaim.AwaitingResponse ? 0 : 1).ThenByDescending(c => c.SentAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<QualityClaim> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.QualityClaims.AsNoTracking().Where(c => c.CustomerId == customerId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Include(c => c.Invoice).Include(c => c.Photos)
            .OrderByDescending(c => c.SentAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<QualityClaimSending>> ListSendingsAsync(Guid claimId, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.QualityClaim && n.RelatedRecordId == claimId && n.Channel != NotificationChannel.InApp)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new QualityClaimSending(n.Channel, n.PhoneNumber, n.DeliveryStatusId, n.DeliveryStatusAt))
            .ToListAsync(cancellationToken);

    private IQueryable<QualityClaim> Detail() => db.QualityClaims.AsNoTracking().AsSplitQuery()
        .Include(c => c.Photos)
        .Include(c => c.Customer)
        .Include(c => c.Invoice).ThenInclude(i => i.Zone)
        .Include(c => c.Invoice).ThenInclude(i => i.Lines).ThenInclude(l => l.Product)
        .Include(c => c.Invoice).ThenInclude(i => i.Lines).ThenInclude(l => l.HarvestLot)
        .Include(c => c.Invoice).ThenInclude(i => i.PriceCuts);
}
