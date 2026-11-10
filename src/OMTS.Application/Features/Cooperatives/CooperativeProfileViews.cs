using System.Text.Json;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// Builds the FARM-01 answers: the profile with its certifications and the preview of the provenance page block
/// (BR-FARM-09), with certification states as of today's business date (BR-FARM-06).
/// </summary>
public sealed class CooperativeProfileViews(ICooperativeRepository cooperatives, IFileStorage storage, TimeProvider timeProvider)
{
    public async Task<CooperativeProfileView> ProfileAsync(Cooperative c, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var stored = await cooperatives.ListCertificationsAsync(c.Id, cancellationToken);
        var certifications = new List<CooperativeCertificationView>();
        foreach (var certification in stored)
        {
            certifications.Add(await CertificationAsync(certification, today, cancellationToken));
        }

        var (logoUrl, coverPhotoUrl) = PublicFileUrls(c);
        var preview = Preview(c, logoUrl, coverPhotoUrl, await PublicCertificationsAsync(stored, now, cancellationToken));
        return new CooperativeProfileView(
            c.Id, c.FullName, c.ShortName, c.TaxCode, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website,
            c.SocialLink, c.IntroductionVi, c.IntroductionEn, c.LegalRepresentative, BusinessLinesOf(c), logoUrl, coverPhotoUrl,
            today, certifications, preview, RowVersionGuard.Encode(c.RowVersion));
    }

    /// <summary>The cooperative block of the provenance page (ORIGIN-01, BR-FARM-09): the same block the FARM-01 preview shows, as of now.</summary>
    public async Task<PublicCooperativePreview> PublicBlockAsync(Cooperative c, CancellationToken cancellationToken)
    {
        var stored = await cooperatives.ListCertificationsAsync(c.Id, cancellationToken);
        var (logoUrl, coverPhotoUrl) = PublicFileUrls(c);
        return Preview(c, logoUrl, coverPhotoUrl, await PublicCertificationsAsync(stored, timeProvider.GetUtcNow(), cancellationToken));
    }

    public async Task<CooperativeCertificationView> CertificationAsync(CooperativeCertification c, DateOnly today, CancellationToken cancellationToken)
    {
        var fileUrl = c.FileUrl is null ? null : await storage.GetReadUrlAsync(c.FileUrl, cancellationToken: cancellationToken);
        return new CooperativeCertificationView(
            c.Id, c.Name, c.Number, c.Issuer, c.ExpiryDate, CertificationStateRules.StateOn(c.ExpiryDate, today), fileUrl,
            RowVersionGuard.Encode(c.RowVersion));
    }

    private static (string? LogoUrl, string? CoverPhotoUrl) PublicFileUrls(Cooperative c) => (
        CooperativePublicFiles.UrlOf(c.LogoUrl, FileCategories.CooperativeLogos, CooperativePublicFiles.LogoFilesPath),
        CooperativePublicFiles.UrlOf(c.CoverPhotoUrl, FileCategories.CooperativeCoverPhotos, CooperativePublicFiles.CoverPhotoFilesPath));

    public static IReadOnlyList<string> BusinessLinesOf(Cooperative c) =>
        string.IsNullOrEmpty(c.BusinessLines) ? [] : JsonSerializer.Deserialize<List<string>>(c.BusinessLines) ?? [];

    /// <summary>
    /// The certifications the provenance page shows, with their files (BR-ORIGIN-01: the page's data holds only what the
    /// page shows): an Expired one is left out with its file, and the signed URL of the others ends before they turn
    /// Expired (<see cref="CertificationStateRules.PublicFileLifetime"/>), so a page opened on the expiry day loses the file
    /// at the business midnight. The file itself stays stored for the Owner.
    /// </summary>
    private async Task<IReadOnlyList<PublicCertification>> PublicCertificationsAsync(
        IEnumerable<CooperativeCertification> certifications, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = BusinessClock.ToBusinessDate(now);
        var shown = new List<PublicCertification>();
        foreach (var c in certifications)
        {
            var state = CertificationStateRules.StateOn(c.ExpiryDate, today);
            if (state == CertificationState.Expired)
            {
                continue;
            }

            var fileUrl = c.FileUrl is null
                ? null
                : await storage.GetReadUrlAsync(c.FileUrl, CertificationStateRules.PublicFileLifetime(c.ExpiryDate, now), cancellationToken);
            shown.Add(new PublicCertification(c.Name, c.Number, c.Issuer, c.ExpiryDate, state, fileUrl));
        }

        return shown;
    }

    /// <summary>
    /// The provenance page block: the current profile without the tax code and internal fields, the English introduction
    /// falling back to the Vietnamese one, and only the certifications that are not Expired.
    /// </summary>
    private static PublicCooperativePreview Preview(
        Cooperative c, string? logoUrl, string? coverPhotoUrl, IReadOnlyList<PublicCertification> certifications) => new(
        c.FullName, c.ShortName, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.SocialLink,
        logoUrl, coverPhotoUrl, c.IntroductionVi, c.IntroductionEn ?? c.IntroductionVi, certifications);
}
