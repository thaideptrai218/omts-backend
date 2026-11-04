using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// SALE-11 – the price list of the Cooperative Owner, who alone writes it (API filter, BR-SALE-05). The name is unique,
/// discontinued products included; only an In Use product is edited; a Discontinued one can be resumed (GBR-01). The audit
/// log is the only price history (BR-SALE-05). Reads: <see cref="ProductReadService"/>.
/// </summary>
public sealed class ProductService(
    IProductRepository products,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(Product);
    private const string ObjectName = "Product";
    private const int MaxNameLength = 200;
    private const int MaxAuditCodeLength = 100;

    /// <summary>The largest amount the column holds (18 digits).</summary>
    private const decimal MaxUnitPrice = 999_999_999_999_999_999m;

    public async Task<Result<ProductSaved>> CreateAsync(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateAsync(values.Name, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var product = new Product { Name = values.Name, UnitPrice = values.UnitPrice };
        products.Add(product);
        audit.Record(new AuditEntry("Product.Created", RecordType) { RecordId = product.Id, RecordCode = AuditCode(product), After = Snapshot(product) });
        if (await SaveAsync(values.Name, null, cancellationToken) is { } error)
        {
            return error;
        }

        return Saved(product);
    }

    /// <summary>SALE-11.1: an In Use product only. A new price applies to invoices created after the save (BR-SALE-05).</summary>
    public async Task<Result<ProductSaved>> UpdateAsync(Guid productId, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is null)
        {
            return AppError.NotFound;
        }

        if (product.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(product);
        }

        var (values, invalid) = Validate(request);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateAsync(values.Name, product.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        if (await rowVersions.CheckAsync(product, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(product);
        product.Name = values.Name;
        product.UnitPrice = values.UnitPrice;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(product));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Product.Updated", RecordType) { RecordId = product.Id, RecordCode = AuditCode(product), Before = changedBefore, After = changedAfter });
            if (await SaveAsync(values.Name, product.Id, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return Saved(product);
    }

    public Task<Result<ProductView>> DiscontinueAsync(Guid productId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(productId, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, "Product.Discontinued", cancellationToken);

    /// <summary>SALE-11.2: back to In Use with the same name, price and history.</summary>
    public Task<Result<ProductView>> ResumeAsync(Guid productId, CancellationToken cancellationToken) =>
        ChangeStatusAsync(productId, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, "Product.Resumed", cancellationToken);

    private async Task<Result<ProductView>> ChangeStatusAsync(Guid productId, int fromStatusId, int toStatusId, string action, CancellationToken cancellationToken)
    {
        var product = await products.GetForEditAsync(productId, cancellationToken);
        if (product is null)
        {
            return AppError.NotFound;
        }

        if (product.StatusId != fromStatusId)
        {
            return WrongStatus(product);
        }

        product.StatusId = toStatusId;
        audit.Record(new AuditEntry(action, RecordType)
        {
            RecordId = product.Id,
            RecordCode = AuditCode(product),
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) },
            After = new { Status = statusCatalog.CodeOf(toStatusId) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(product);
    }

    /// <summary>MSG-GEN-02 with the id of the other product that has this name, which the Owner may open or resume.</summary>
    private async Task<AppError?> DuplicateAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken) =>
        await products.FindByNameAsync(name, exceptProductId, cancellationToken) is { } same ? AppError.AlreadyExists(ObjectName, same.Id) : null;

    /// <summary>The second of two parallel saves with one name gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await DuplicateAsync(name, exceptProductId, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }

            throw;
        }
    }

    private AppError WrongStatus(Product product) =>
        AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {product.Name}", statusCatalog.DisplayNameOf(product.StatusId)));

    private static (Values Values, List<string> Invalid) Validate(SaveProductRequest request)
    {
        var invalid = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            invalid.Add("Name");
        }

        if (request.UnitPrice is not { } price || price <= 0 || price > MaxUnitPrice || decimal.Truncate(price) != price)
        {
            invalid.Add("Unit price");
        }

        return (new Values(name, request.UnitPrice ?? 0), invalid);
    }

    private ProductSaved Saved(Product product)
    {
        var saved = UserMessages.Saved(ObjectName);
        return new ProductSaved(View(product), saved.Text, saved.Code);
    }

    private ProductView View(Product p) =>
        new(p.Id, p.Name, p.UnitPrice, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), RowVersionGuard.Encode(p.RowVersion));

    private static object Snapshot(Product p) => new { p.Name, p.UnitPrice };

    // AuditEvent.RecordCode holds 100 characters; product names may have 200.
    private static string AuditCode(Product p) => p.Name.Length <= MaxAuditCodeLength ? p.Name : p.Name[..MaxAuditCodeLength];

    private sealed record Values(string Name, decimal UnitPrice);
}
