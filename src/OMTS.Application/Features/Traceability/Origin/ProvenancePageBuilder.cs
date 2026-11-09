using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Cooperatives;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>Builds the provenance page from the rows it read (ORIGIN-01.0 steps 2-4, ORIGIN-01.1, ORIGIN-01.2, ORIGIN-01.0.E2). Pure: it reads and writes nothing.</summary>
public static class ProvenancePageBuilder
{
    /// <summary>ORIGIN-01.0.E2: a cancelled invoice shows the cooperative block and MSG-ORIGIN-04 only, without its lots, because the sale did not happen (BR-ORIGIN-05).</summary>
    public static ProvenancePage Cancelled(PublicCooperativePreview cooperative, string language)
    {
        var message = UserMessages.ProvenanceInvoiceCancelled(cooperative.PhoneNumber).In(language);
        return new ProvenancePage(
            language, ProvenancePageStatus.Cancelled, message.Text, message.Code, message.Parameters, null, Cooperative(cooperative, language), []);
    }

    /// <summary>
    /// The lots of the invoice that are Published or Withdrawn, in line order, each with the products sold from it on this invoice; Not published lots are left out
    /// (BR-ORIGIN-05). None: MSG-ORIGIN-02. The lines of a lot whose returned oysters were thrown away (CLAIM-03) are not among <paramref name="invoice"/>'s lines, so it is not listed.
    /// </summary>
    /// <param name="seedings">The sessions of the Published lots.</param>
    /// <param name="photoUrls">The signed read URL of each photo key of the Published lots.</param>
    public static ProvenancePage Build(
        ProvenanceInvoiceRow invoice,
        IReadOnlyList<ProvenanceLotRow> lots,
        IReadOnlyList<ProvenanceSeedingRow> seedings,
        IReadOnlyDictionary<string, string> photoUrls,
        PublicCooperativePreview cooperative,
        string language)
    {
        var byId = lots.ToDictionary(l => l.Id);
        var sessions = seedings.ToLookup(s => s.LotId);
        var listed = invoice.Lines
            .OrderBy(l => l.LineNumber)
            .GroupBy(l => l.HarvestLotId)
            .Where(g => byId.TryGetValue(g.Key, out var lot) && lot.OriginState != HarvestLotOriginState.NotPublished)
            .Select(g => Lot(byId[g.Key], g.Select(l => l.ProductName).Distinct().ToList(), sessions[g.Key], photoUrls, cooperative, language))
            .ToList();
        var block = Cooperative(cooperative, language);
        var page = new ProvenancePageInvoice(invoice.Code, invoice.InvoiceDate);
        if (listed.Count == 0)
        {
            var none = UserMessages.ProvenanceNoLots(cooperative.PhoneNumber).In(language);
            return new ProvenancePage(language, ProvenancePageStatus.NoLots, none.Text, none.Code, none.Parameters, page, block, []);
        }

        return new ProvenancePage(language, ProvenancePageStatus.Ok, null, null, null, page, block, listed);
    }

    /// <summary>BR-ORIGIN-03: days of farming run from the earliest seeding date of the lot's lanes to its harvest date; null without a session.</summary>
    public static int? DaysOfFarming(DateOnly harvestDate, IEnumerable<DateOnly> seedingDates)
    {
        var dates = seedingDates.ToList();
        return dates.Count == 0 ? null : harvestDate.DayNumber - dates.Min().DayNumber;
    }

    /// <summary>BR-ORIGIN-03: the raft's map location, or the zone's when the raft has none.</summary>
    public static ProvenanceMapPoint? Map(ProvenanceLotRow lot) =>
        lot is { RaftLatitude: { } raftLat, RaftLongitude: { } raftLng } ? new ProvenanceMapPoint(raftLat, raftLng, ProvenanceMapSource.Raft)
        : lot is { ZoneLatitude: { } zoneLat, ZoneLongitude: { } zoneLng } ? new ProvenanceMapPoint(zoneLat, zoneLng, ProvenanceMapSource.Zone)
        : null;

    private static ProvenanceLot Lot(
        ProvenanceLotRow lot, IReadOnlyList<string> products, IEnumerable<ProvenanceSeedingRow> sessions, IReadOnlyDictionary<string, string> photoUrls,
        PublicCooperativePreview cooperative, string language)
    {
        if (lot.OriginState == HarvestLotOriginState.Withdrawn)
        {
            // ORIGIN-01.1: the date and the cooperative phone, without the origin information and never the reason (BR-TRACE-06).
            var withdrawalDate = BusinessClock.ToBusinessDate(lot.WithdrawnAt!.Value);
            var message = UserMessages.ProvenanceLotWithdrawn(cooperative.ShortName ?? cooperative.FullName, lot.LotCode, withdrawalDate, cooperative.PhoneNumber).In(language);
            return new ProvenanceLot(lot.LotCode, products, lot.OriginState, withdrawalDate, message.Text, message.Code, message.Parameters, null);
        }

        var seedings = sessions
            .Select(s => new ProvenanceSeeding(s.SeedingDate, s.SeedItemName))
            .Distinct()
            .OrderBy(s => s.SeedingDate).ThenBy(s => s.SeedItemName, StringComparer.Ordinal)
            .ToList();
        var origin = new ProvenanceOrigin(
            lot.HarvestDate,
            lot.RaftCode,
            lot.ZoneName,
            Map(lot),
            seedings,
            DaysOfFarming(lot.HarvestDate, seedings.Select(s => s.SeedingDate)),
            Urls(lot.HarvestPhotoKeys, photoUrls),
            Urls(lot.RaftPhotoKeys, photoUrls));
        return new ProvenanceLot(lot.LotCode, products, lot.OriginState, null, null, null, null, origin);
    }

    private static List<string> Urls(IEnumerable<string> keys, IReadOnlyDictionary<string, string> photoUrls) =>
        keys.Where(photoUrls.ContainsKey).Select(k => photoUrls[k]).ToList();

    // BR-ORIGIN-02: the introduction in the page language, the Vietnamese one when the English one is empty (the preview already falls back).
    private static ProvenanceCooperative Cooperative(PublicCooperativePreview c, string language) => new(
        c.FullName, c.ShortName, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.SocialLink, c.LogoUrl, c.CoverPhotoUrl,
        language == ProvenanceLanguages.English && !string.IsNullOrWhiteSpace(c.IntroductionEn) ? c.IntroductionEn : c.IntroductionVi,
        c.Certifications);
}
