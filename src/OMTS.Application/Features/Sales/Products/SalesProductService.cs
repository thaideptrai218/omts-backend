using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Products.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>The product list a trader picks from when sending a purchase request (SALE-01), and the way the Mini App lets the Owner write the catalogue, which goes through <see cref="ProductService"/>.</summary>
public sealed class SalesProductService(ISalesProductRepository products, IAccountRepository accounts, ICurrentUser user, ProductService catalogue, RowVersionGuard versions, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
    public async Task<Result<IReadOnlyList<SalesProductView>>> ListAsync(bool includeUnavailable, CancellationToken ct)
    {
        var account = user.AccountId is { } id ? await accounts.GetByIdAsync(id, ct) : null;
        if (account?.SubjectType is not (SubjectType.Trader or SubjectType.CooperativeOwner)) return AppError.Forbidden;
        if (includeUnavailable && account.SubjectType != SubjectType.CooperativeOwner) return AppError.Forbidden;
        return Result<IReadOnlyList<SalesProductView>>.Success((await products.ListAsync(!includeUnavailable, Today, ct)).Select(View).ToList());
    }
    /// <summary>
    /// The Mini App writes the product catalogue through <see cref="ProductService"/>, so it keeps the rules of the web: a price above 0 (BR-SALE-05), a unique name and code, a hidden (deleted) product is not found, and only an In Use
    /// product is edited. A price left out keeps the price of the product, so a product that has none still needs one. In Use / Discontinued is changed as the web does, by resuming or discontinuing.
    /// </summary>
    public async Task<Result<SalesProductView>> SaveAsync(Guid? id, SaveSalesProduct input, CancellationToken ct)
    {
        var account = user.AccountId is { } caller ? await accounts.GetByIdAsync(caller, ct) : null;
        if (account?.SubjectType != SubjectType.CooperativeOwner) return AppError.Forbidden;

        var existing = id is { } value ? await products.GetAsync(value, ct) : null;
        if (id is not null && existing is not { IsHidden: false }) return AppError.NotFound;

        var request = ToProductRequest(existing, input);
        var wantsInUse = input.InUse;
        Guid savedId;
        if (existing is null)
        {
            var created = await catalogue.CreateAsync(request, ct);
            if (!created.IsSuccess) return created.Error!;
            savedId = created.Value!.Product.Id;
        }
        else
        {
            savedId = existing.Id;
            // A Discontinued product is resumed first, because only an In Use product is edited.
            if (existing.StatusId != StatusIds.Usage.InUse && wantsInUse)
            {
                if (await versions.CheckAsync(existing, input.RowVersion, ct) is { } stale) return stale;
                var resumed = await catalogue.ResumeAsync(savedId, ct);
                if (!resumed.IsSuccess) return resumed.Error!;
                // The resume saved the row, so the form is now checked against the version it left.
                request = request with { RowVersion = RowVersionGuard.Encode(existing.RowVersion) };
            }

            var updated = await catalogue.UpdateAsync(savedId, request, ct);
            if (!updated.IsSuccess) return updated.Error!;
        }

        if (!wantsInUse && (existing is null || existing.StatusId == StatusIds.Usage.InUse))
        {
            var discontinued = await catalogue.DiscontinueAsync(savedId, ct);
            if (!discontinued.IsSuccess) return discontinued.Error!;
        }

        var row = await products.GetAsync(savedId, ct);
        return row is null ? AppError.NotFound : View(row);
    }

    /// <summary>
    /// The web form of the product the Mini App entry stands for. The fields the Mini App does not have (group, note, kg limits, size, units) are those of <paramref name="existing"/>, so editing there does not clear them;
    /// the units are left as they are.
    /// </summary>
    internal static SaveProductRequest ToProductRequest(Product? existing, SaveSalesProduct input) => new(
        input.Name,
        input.UnitPrice ?? existing?.UnitPrice,
        input.RowVersion,
        input.Code,
        existing?.CategoryId,
        input.Description,
        existing?.Note,
        existing?.MinKg,
        existing?.MaxKg,
        Units: null,
        SizeMinCm: existing?.SizeMinCm,
        SizeMaxCm: existing?.SizeMaxCm);

    public static bool Available(Product p, DateOnly today) =>
        p.StatusId == StatusIds.Usage.InUse && (p.ValidFrom is null || p.ValidFrom <= today) && (p.ValidTo is null || p.ValidTo >= today);
    private SalesProductView View(Product p) => new(p.Id, p.Code, p.Name, p.Description,
        p.StatusId == StatusIds.Usage.InUse, Available(p, Today), RowVersionGuard.Encode(p.RowVersion));
}
