namespace OMTS.Application.Features.Files;

/// <summary>
/// Storage categories, the first segment of a storage key. Each category belongs to one record column: the orphan
/// file clean-up deletes what that column no longer points to, so another kind of file needs a category of its own.
/// </summary>
public static class FileCategories
{
    /// <summary>FARM-04 raft photo, <c>Raft.PhotoUrl</c>.</summary>
    public const string RaftPhotos = "raft-photos";

    /// <summary>FARM-03 license document, <c>ZoneComplianceDocument.EvidenceFileUrl</c>.</summary>
    public const string ZoneLicenses = "zone-licenses";
}
