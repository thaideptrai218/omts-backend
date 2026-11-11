using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// Reads of the kg ledger of a product: its movements ("Nhập – xuất" tab) with the running balance (worked out in the order of time then write order, so a document dated in the past stays consistent), the kg
/// on each harvest lot ("Lô thu hoạch" tab) and the price history ("Lịch sử giá" tab). Readable by whoever reads the product catalogue.
/// </summary>
public sealed class ProductMovementService(
    IProductRepository products, IProductStockRepository stock, IAccountRepository accounts, ProductAccess access, ISpreadsheetWriter spreadsheets, TimeProvider timeProvider)
{
    private const int MaxPageSize = 100;

    public async Task<Result<ProductMovementPage>> MovementsAsync(Guid productId, ProductMovementQuery query, CancellationToken cancellationToken)
    {
        var read = await ReadMovementsAsync(productId, query, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (rows, kindOf) = read.Value;
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        return new ProductMovementPage(rows.Skip((page - 1) * pageSize).Take(pageSize).Select(r => View(r, kindOf)).ToList(), page, pageSize, rows.Count, ProductRules.BaseUnitName);
    }

    /// <summary>The movements as filtered by the query (same period, kind and reversal rules and the same zone scope as the tab), every page, as .xlsx.</summary>
    public async Task<Result<ExportedFile>> ExportAsync(Guid productId, ProductMovementQuery query, CancellationToken cancellationToken)
    {
        var read = await ReadMovementsAsync(productId, query, cancellationToken);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        var (rows, kindOf) = read.Value;
        var product = (await products.GetByIdsAsync([productId], cancellationToken)).First();
        string[] headers = ["Document", "Type", "Time", "Party", "Party code", "Lot", "Price", "Quantity (kg)", "Balance (kg)", "Unit"];
        var data = rows.Select(r => (IReadOnlyList<object?>)new List<object?>
        {
            r.Movement.DocumentCode, View(r, kindOf).Kind.ToString(), r.Movement.OccurredAt.ToOffset(BusinessClock.UtcOffset).DateTime, r.Movement.PartyName, r.Movement.PartyCode,
            r.Movement.LotCode, r.Movement.TransactionPrice, r.Movement.QuantityKg, r.BalanceAfter, ProductRules.BaseUnitName,
        }).ToList();
        try
        {
            return new ExportedFile($"{product.Code}-movements-{timeProvider.Today():yyyyMMdd}.xlsx", ISpreadsheetWriter.ContentType, spreadsheets.Write([new SpreadsheetSheet("Movements", headers, data)]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    /// <summary>The kg of the product per harvest lot, the latest harvest first.</summary>
    public async Task<Result<ProductLotsView>> LotsAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await FindAsync(productId, cancellationToken);
        if (!product.IsSuccess)
        {
            return product.Error!;
        }

        var lots = (await stock.LotsOfProductAsync(productId, await access.ZoneScopeAsync(cancellationToken), cancellationToken))
            .Select(l => new ProductLotView(l.LotId, l.LotCode, l.ZoneId, l.ZoneCode, l.RaftCode, l.HarvestDate, l.HarvestedAt, l.LotStatusId, l.HarvestedKg, l.SoldKg, l.DisposedKg, l.ReturnedKg, l.RemainingKg)).ToList();
        return new ProductLotsView(productId, product.Value!.Code, lots, lots.Sum(l => l.RemainingKg));
    }

    /// <summary>Every price of every unit of the product, newest first, or of one unit (the shared unit id); the first row of a unit is its price when it was added (no old price).</summary>
    public async Task<Result<IReadOnlyList<ProductPriceChangeView>>> PriceHistoryAsync(Guid productId, Guid? unitId, CancellationToken cancellationToken)
    {
        var product = await FindAsync(productId, cancellationToken);
        if (!product.IsSuccess)
        {
            return product.Error!;
        }

        var changes = await products.PriceHistoryAsync(productId, unitId, cancellationToken);
        var people = changes.Select(c => c.ChangedByAccountId).OfType<Guid>().Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(people, cancellationToken);
        return Result<IReadOnlyList<ProductPriceChangeView>>.Success(
            changes.Select(c => new ProductPriceChangeView(c.Id, c.UnitId, c.UnitCode, c.UnitName, c.OldPrice, c.NewPrice, c.ChangedAt, c.ChangedByAccountId, c.ChangedByAccountId is { } id ? names.GetValueOrDefault(id) : null)).ToList());
    }

    private async Task<Result<Product>> FindAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (!ProductAccess.IsCatalogueReader(await access.ReaderAsync(cancellationToken)))
        {
            return AppError.Forbidden;
        }

        return (await products.GetByIdsAsync([productId], cancellationToken)).FirstOrDefault() is { } product ? product : AppError.NotFound;
    }

    private static ProductMovementView View(ProductMovementRow r, IReadOnlyDictionary<Guid, ProductStockMovementKind> kindOf)
    {
        var m = r.Movement;
        var effective = m.Kind == ProductStockMovementKind.Reversal && m.ReversesMovementId is { } undone && kindOf.TryGetValue(undone, out var original) ? original : m.Kind;
        var documentKind = effective switch
        {
            ProductStockMovementKind.Sale => "SalesInvoice",
            ProductStockMovementKind.Disposal => "ProductDisposal",
            _ => "HarvestLot",
        };
        return new ProductMovementView(
            m.Id, m.Sequence, m.Kind, documentKind, m.DocumentId, m.DocumentCode, m.OccurredAt, m.QuantityKg, r.BalanceAfter, m.TransactionPrice, m.PartyName, m.PartyCode, m.HarvestLotId, m.LotCode, m.ReversesMovementId);
    }

    /// <summary>The movements that match the query, newest first, each with the balance after it in time order.</summary>
    private async Task<Result<(IReadOnlyList<ProductMovementRow> Rows, IReadOnlyDictionary<Guid, ProductStockMovementKind> KindOf)>> ReadMovementsAsync(
        Guid productId, ProductMovementQuery query, CancellationToken cancellationToken)
    {
        var product = await FindAsync(productId, cancellationToken);
        if (!product.IsSuccess)
        {
            return product.Error!;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        if (query.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return AppError.Invalid("Kind");
        }

        var from = query.From is { } f ? BusinessClock.FromLocal(f.ToDateTime(TimeOnly.MinValue)) : (DateTimeOffset?)null;
        var before = query.To is { } t ? BusinessClock.FromLocal(t.AddDays(1).ToDateTime(TimeOnly.MinValue)) : (DateTimeOffset?)null;
        var all = await stock.ForProductAsync(productId, query.IncludeReversals, await access.ZoneScopeAsync(cancellationToken), cancellationToken);
        // An adjustment counts as the kind of the row it undoes, so the kind filter keeps the adjustments of that kind when they are shown.
        var kindOf = all.ToDictionary(r => r.Movement.Id, r => r.Movement.Kind);
        bool OfKind(ProductStockMovement m) =>
            query.Kind is null || m.Kind == query.Kind
            || (m.Kind == ProductStockMovementKind.Reversal && m.ReversesMovementId is { } undone && kindOf.TryGetValue(undone, out var original) && original == query.Kind);
        IReadOnlyList<ProductMovementRow> rows = all
            .Where(r => (from is null || r.Movement.OccurredAt >= from) && (before is null || r.Movement.OccurredAt < before) && OfKind(r.Movement))
            .OrderByDescending(r => r.Movement.OccurredAt).ThenByDescending(r => r.Movement.Sequence).ToList();
        return (rows, kindOf);
    }
}
