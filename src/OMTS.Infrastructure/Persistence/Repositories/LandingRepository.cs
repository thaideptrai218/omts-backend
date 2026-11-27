using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class LandingRepository(OmtsDbContext db) : ILandingRepository
{
    public Task<List<LandingPhoto>> PhotosAsync(CancellationToken cancellationToken) =>
        db.LandingPhotos.OrderBy(p => p.Section).ThenBy(p => p.SortOrder).ToListAsync(cancellationToken);

    public void Add(LandingPhoto photo) => db.LandingPhotos.Add(photo);

    public void Remove(LandingPhoto photo) => db.LandingPhotos.Remove(photo);

    public async Task<IReadOnlyList<ShowcaseProductRow>> ProductsOnSaleAsync(DateOnly today, CancellationToken cancellationToken) =>
        await Rows(db.Products.Where(p => !p.IsHidden && p.StatusId == StatusIds.Usage.InUse
                && (p.ValidFrom == null || p.ValidFrom <= today) && (p.ValidTo == null || p.ValidTo >= today)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ShowcaseProductRow>> ProductsByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken) =>
        names.Count == 0 ? [] : await Rows(db.Products.Where(p => names.Contains(p.Name))).ToListAsync(cancellationToken);

    public Task<string?> QrTokenOfAsync(string invoiceCode, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsNoTracking()
            .Where(i => i.Code == invoiceCode && i.StatusId != StatusIds.SalesInvoice.Cancelled)
            .Select(i => i.QrToken)
            .FirstOrDefaultAsync(cancellationToken);

    private static IQueryable<ShowcaseProductRow> Rows(IQueryable<Product> products) =>
        products.AsNoTracking().AsSplitQuery()
            .OrderBy(p => p.Name)
            .Select(p => new ShowcaseProductRow(p.Name, p.Description, p.SizeMinCm, p.SizeMaxCm, p.Photos.OrderBy(x => x.SortOrder).Select(x => x.StorageKey).ToList()));
}
