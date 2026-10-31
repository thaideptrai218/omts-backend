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
        var today = timeProvider.Today();
        var certifications = new List<CooperativeCertificationView>();
        foreach (var certification in await cooperatives.ListCertificationsAsync(c.Id, cancellationToken))
        {
            certifications.Add(await CertificationAsync(certification, today, cancellationToken));
        }

        var logoUrl = CooperativePublicFiles.UrlOf(c.LogoUrl, FileCategories.CooperativeLogos, CooperativePublicFiles.LogoFilesPath);
        var coverPhotoUrl = CooperativePublicFiles.UrlOf(c.CoverPhotoUrl, FileCategories.CooperativeCoverPhotos, CooperativePublicFiles.CoverPhotoFilesPath);
        return new CooperativeProfileView(
            c.Id, c.FullName, c.ShortName, c.TaxCode, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website,
            c.SocialLink, c.IntroductionVi, c.IntroductionEn, c.LegalRepresentative, BusinessLinesOf(c), logoUrl, coverPhotoUrl,
            today, certifications, Preview(c, logoUrl, coverPhotoUrl, certifications), RowVersionGuard.Encode(c.RowVersion));
    }

    public async Task<CooperativeCertificationView> CertificationAsync(CooperativeCertification c, DateOnly today, CancellationToken cancellationToken)
    {
        var fileUrl = c.FileUrl is null ? null : await storage.GetReadUrlAsync(c.FileUrl, cancellationToken: cancellationToken);
        return new CooperativeCertificationView(
            c.Id, c.Name, c.Number, c.Issuer, c.ExpiryDate, CertificationStateRules.StateOn(c.ExpiryDate, today), fileUrl,
            RowVersionGuard.Encode(c.RowVersion));
    }

    public static IReadOnlyList<string> BusinessLinesOf(Cooperative c) =>
        string.IsNullOrEmpty(c.BusinessLines) ? [] : JsonSerializer.Deserialize<List<string>>(c.BusinessLines) ?? [];

    /// <summary>
    /// The provenance page block: the current profile without the tax code and internal fields, the English introduction
    /// falling back to the Vietnamese one, and only the certifications that are not Expired (no file).
    /// </summary>
    private static PublicCooperativePreview Preview(
        Cooperative c, string? logoUrl, string? coverPhotoUrl, IEnumerable<CooperativeCertificationView> certifications) => new(
        c.FullName, c.ShortName, c.YearEstablished, c.OfficeAddress, c.PhoneNumber, c.Email, c.Website, c.SocialLink,
        logoUrl, coverPhotoUrl, c.IntroductionVi, c.IntroductionEn ?? c.IntroductionVi,
        certifications
            .Where(x => x.State != CertificationState.Expired)
            .Select(x => new PublicCertification(x.Name, x.Number, x.Issuer, x.ExpiryDate, x.State))
            .ToList());
}
