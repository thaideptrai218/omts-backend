using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Traceability;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Traceability.Origin.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>
/// The public provenance page a QR code opens (BR-ORIGIN-01): anyone, with no login. It lists the Published and Withdrawn lots of the invoice (BR-ORIGIN-05): a Published lot with its
/// origin from the seed to the sale, a Withdrawn one only as withdrawn, its reason staying internal (BR-TRACE-06). It never shows the customer, the staff or a price (decided
/// 10/11/2026). A token that is unknown, of a cancelled invoice, or whose lots were never published answers 404. Nothing is written.
/// </summary>
public sealed class PublicOriginService(IPublicOriginQuery origins)
{
    /// <summary>A token of the sale is 22 characters (16 random bytes in base64url); anything far longer is refused before any read.</summary>
    private const int MaxTokenLength = 64;

    public async Task<Result<PublicOriginView>> GetAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return AppError.NotFound;
        }

        var sale = await origins.SaleAsync(token, cancellationToken);
        if (sale is null)
        {
            return AppError.NotFound;
        }

        var lots = (await origins.LotsAsync(sale.Lines.Select(l => l.HarvestLotId).Distinct().ToList(), cancellationToken))
            .Where(l => l.OriginState != HarvestLotOriginState.NotPublished)
            .ToList();
        return lots.Count == 0 ? AppError.NotFound : new PublicOriginView(sale.CooperativeName, lots.Select(l => View(sale, l)).ToList());
    }

    private static PublicOriginLotView View(PublicOriginSale sale, PublicOriginLotFacts lot)
    {
        if (lot.OriginState == HarvestLotOriginState.Withdrawn)
        {
            return new PublicOriginLotView(lot.LotCode, lot.OriginState, lot.WithdrawnAt, null);
        }

        var lines = sale.Lines.Where(l => l.HarvestLotId == lot.Id).ToList();
        var seedLots = lot.Seedings.Select(s => new PublicOriginSeedLotView(s.ItemName, s.SupplierLotCode, s.SupplierName, s.ReceiptDate)).Distinct().ToList();
        var water = new PublicOriginWaterView(
            lot.Water.Count, Round(lot.Water.AverageSalinity, 1), Round(lot.Water.AverageTemperature, 1), Round(lot.Water.AveragePh, 2));
        return new PublicOriginLotView(lot.LotCode, lot.OriginState, null, new PublicOriginLotDetail(
            lines.Select(l => l.ProductName).Distinct().ToList(),
            lot.ZoneName,
            lot.RaftCode,
            lot.CycleStartDate,
            seedLots,
            lot.Seedings.Count == 0 ? null : lot.Seedings.Min(s => s.SeedingDate),
            lot.Seedings.Count == 0 ? null : lot.Seedings.Max(s => s.SeedingDate),
            lot.LaneCount,
            lot.CultivationLogCount,
            water,
            lot.HarvestDate,
            lot.CageCount,
            lot.EstimatedKg,
            sale.InvoiceDate,
            lines.Sum(l => l.Kg),
            sale.WeighedCages.GetValueOrDefault(lot.Id)));
    }

    private static decimal? Round(decimal? value, int decimals) => value is { } number ? Math.Round(number, decimals, MidpointRounding.AwayFromZero) : null;
}
