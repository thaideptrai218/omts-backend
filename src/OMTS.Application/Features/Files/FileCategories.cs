namespace OMTS.Application.Features.Files;

/// <summary>
/// Storage categories, the first segment of a storage key. Each category belongs to one record column: the orphan
/// file clean-up deletes what that column no longer points to, so another kind of file needs a category of its own.
/// </summary>
public static class FileCategories
{
    /// <summary>FARM-04 raft photos, <c>RaftPhoto.FileKey</c>.</summary>
    public const string RaftPhotos = "raft-photos";

    /// <summary>FARM-03 license document, <c>ZoneComplianceDocument.EvidenceFileUrl</c>.</summary>
    public const string ZoneLicenses = "zone-licenses";

    /// <summary>
    /// FARM-01 logo, <c>Cooperative.LogoUrl</c>. Never cleaned up: every logo file is kept, since published provenance
    /// versions may show an older one.
    /// </summary>
    public const string CooperativeLogos = "cooperative-logos";

    /// <summary>FARM-01 cover photo of the provenance page, <c>Cooperative.CoverPhotoUrl</c>.</summary>
    public const string CooperativeCoverPhotos = "cooperative-covers";

    /// <summary>FARM-01.1 certificate file, <c>CooperativeCertification.FileUrl</c>.</summary>
    public const string CooperativeCertifications = "cooperative-certifications";

    /// <summary>CYCLE-04 photos of a cultivation log, <c>CultivationLogPhoto.FileKey</c>. Staff only, never public (BR-FARM-09).</summary>
    public const string CultivationLogPhotos = "cultivation-log-photos";

    /// <summary>HARVEST-01 photos of a harvest lot, <c>HarvestLotPhoto.FileKey</c>.</summary>
    public const string HarvestLotPhotos = "harvest-lot-photos";
}
