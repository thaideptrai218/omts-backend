using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// The VAT rate a purchase line took, as it was then: the rate, its name and its percent. A line keeps the snapshot, so editing a rate later changes no document that exists. All null is a line
/// without a rate (made before input VAT existed); <see cref="Percent"/> is null on KCT. Money math reads <see cref="PercentOrZero"/>.
/// </summary>
public sealed record VatSnapshot(Guid? RateId, string? Name, decimal? Percent)
{
    /// <summary>A line made before input VAT existed: no rate, no VAT.</summary>
    public static readonly VatSnapshot None = new(null, null, null);

    /// <summary>The percent the money math uses: none, KCT and 0% are all 0.</summary>
    public decimal PercentOrZero => Percent ?? 0m;

    public static VatSnapshot Of(VatRate rate) => new(rate.Id, rate.Name, rate.Percent);

    public static VatSnapshot Of(PurchaseReceiptLine line) => new(line.VatRateId, line.VatRateName, line.VatPercent);

    public static VatSnapshot Of(PurchaseReturnLine line) => new(line.VatRateId, line.VatRateName, line.VatPercent);

    public void ApplyTo(PurchaseReceiptLine line)
    {
        line.VatRateId = RateId;
        line.VatRateName = Name;
        line.VatPercent = Percent;
    }

    public void ApplyTo(PurchaseReturnLine line)
    {
        line.VatRateId = RateId;
        line.VatRateName = Name;
        line.VatPercent = Percent;
    }

    /// <summary>
    /// The rate of a receipt line. A seed item is always KCT. A rate sent takes the percent it has now, unless it is the rate the kept line of the same item already has (the screen sends back what
    /// it read, and the line keeps the percent it took). Without a rate sent, a kept line of the same item keeps its snapshot (none stays none, so an old receipt is not taxed by an edit); any
    /// other line takes the current rate of the item.
    /// </summary>
    /// <param name="catalogue">Every rate by id; the KCT rate is always in it.</param>
    public static VatSnapshot ForReceiptLine(Guid? requestedRateId, FarmingInputItem? item, PurchaseReceiptLine? kept, IReadOnlyDictionary<Guid, VatRate> catalogue)
    {
        var keptSnapshot = kept is null ? None : Of(kept);
        if (item is null)
        {
            return requestedRateId is { } sent && sent != kept?.VatRateId && catalogue.TryGetValue(sent, out var chosen) ? Of(chosen) : keptSnapshot;
        }

        if (item.Group == InputItemGroup.Seed)
        {
            return Of(catalogue[VatRateIds.Kct]);
        }

        var keepsLine = kept is not null && kept.ItemId == item.Id;
        if (requestedRateId is { } requested)
        {
            if (keepsLine && requested == kept!.VatRateId)
            {
                return keptSnapshot;
            }

            if (catalogue.TryGetValue(requested, out var rate))
            {
                return Of(rate);
            }
        }

        return keepsLine ? keptSnapshot : ItemRate(item, catalogue);
    }

    /// <summary>The current rate of an item, as a line of a new document takes it.</summary>
    public static VatSnapshot ItemRate(FarmingInputItem item, IReadOnlyDictionary<Guid, VatRate> catalogue) =>
        catalogue.TryGetValue(item.PurchaseVatRateId, out var rate) ? Of(rate) : Of(catalogue[VatRateIds.Kct]);
}
