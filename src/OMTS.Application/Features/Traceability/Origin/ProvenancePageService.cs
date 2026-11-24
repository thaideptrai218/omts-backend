using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cooperatives;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>
/// ORIGIN-01 View product origin: the public provenance page of the sales invoice whose QR code the link carries. Anyone sees it without logging in, and a logged-in user sees the
/// same page (BR-ORIGIN-01). It always reads the current data (BR-TRACE-05) and changes nothing (POST-2).
/// </summary>
public sealed partial class ProvenancePageService(
    IProvenancePageQuery pages,
    ICooperativeRepository cooperatives,
    CooperativeProfileViews cooperativeViews,
    IFileStorage storage)
{
    /// <summary>
    /// ORIGIN-01.0: the page in <paramref name="lang"/> ("vi" by default). An unknown QR code, or a link that is no QR code at all, is 404 MSG-ORIGIN-03 in the page language,
    /// so nothing tells apart a token that was never given from a malformed one (ORIGIN-01.0.E1); a cancelled invoice is the cooperative block with MSG-ORIGIN-04 (ORIGIN-01.0.E2).
    /// </summary>
    public async Task<Result<ProvenancePage>> GetAsync(string qrToken, string? lang, CancellationToken cancellationToken)
    {
        var language = ProvenanceLanguages.Of(lang);
        var unknown = new AppError(ErrorKind.NotFound, UserMessages.ProvenanceUnknownQrCode.In(language));
        if (!TokenShape().IsMatch(qrToken) || await pages.FindInvoiceAsync(qrToken, cancellationToken) is not { } invoice)
        {
            return unknown;
        }

        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return unknown;
        }

        var block = await cooperativeViews.PublicBlockAsync(cooperative, cancellationToken);
        if (invoice.IsCancelled)
        {
            return ProvenancePageBuilder.Cancelled(block, language);
        }

        var lots = await pages.LotsAsync(invoice.Lines.Select(l => l.HarvestLotId).Distinct().ToList(), cancellationToken);
        var published = lots.Where(l => l.OriginState == HarvestLotOriginState.Published).ToList();
        IReadOnlyList<ProvenanceSeedingRow> seedings = published.Count == 0 ? [] : await pages.SeedingsAsync(published.Select(l => l.Id).ToList(), cancellationToken);
        var photoUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in published.SelectMany(l => l.HarvestPhotoKeys.Concat(l.RaftPhotoKeys)).Distinct())
        {
            photoUrls[key] = await storage.GetReadUrlAsync(key, PhotoLinkLifetime, cancellationToken);
        }

        return ProvenancePageBuilder.Build(invoice, lots, seedings, photoUrls, block, language);
    }

    // The page is never cached, but a photo the browser loads later (a gallery opened, a lazy image) must still open: links live as long as the page is read (design §3).
    private static readonly TimeSpan PhotoLinkLifetime = TimeSpan.FromHours(1);

    // The shape of every QR code SALE-05 gives: 128 random bits in base64url, 22 characters (BR-TRACE-02). Anything else is not looked up.
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{22}\z")]
    private static partial Regex TokenShape();
}
