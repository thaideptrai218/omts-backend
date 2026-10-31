using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// FARM-01 cover photo of the provenance page (Cooperative Owner only, checked by the API filter). Like the logo it is
/// replaced at once, but only the current file is kept: the replaced or removed file is deleted once the change is
/// saved, and a file left behind is deleted by the orphan file clean-up (<see cref="UploadedFileSaver"/>). A storage
/// failure saves nothing (MSG-GEN-03, GFR-01).
/// </summary>
public sealed class CooperativeCoverPhotoService(
    ICooperativeRepository cooperatives,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    CooperativeProfileViews views,
    AuditLogService audit)
{
    /// <summary>Replaces the cover photo (PNG/JPEG/WebP, 5 MB).</summary>
    public async Task<Result<CooperativeProfileView>> UploadAsync(IncomingFile file, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (cooperative is null)
        {
            return AppError.NotFound;
        }

        var checkedFile = FileValidator.Validate([file], FileRules.CoverPhoto);
        if (!checkedFile.IsValid)
        {
            return AppError.Invalid("Cover photo");
        }

        var uploaded = await storage.UploadAsync(checkedFile.Files[0], FileCategories.CooperativeCoverPhotos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        return await SetAsync(cooperative, uploaded.Key, cancellationToken);
    }

    public async Task<Result<CooperativeProfileView>> RemoveAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) switch
        {
            null => AppError.NotFound,
            // Nothing to remove: no change, no audit row.
            { CoverPhotoUrl: null } cooperative => await views.ProfileAsync(cooperative, cancellationToken),
            var cooperative => await SetAsync(cooperative, null, cancellationToken),
        };

    /// <summary>
    /// The current cover photo by its path below the category ("2026/10/{id}.jpg"), or null: a replaced file, or a
    /// file of another category, is never served.
    /// </summary>
    public async Task<(Stream Content, string ContentType)?> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        var key = CooperativePublicFiles.KeyOf(path, FileCategories.CooperativeCoverPhotos);
        if (key is null || (await cooperatives.GetCurrentAsync(cancellationToken))?.CoverPhotoUrl != key)
        {
            return null;
        }

        return await CooperativePublicFiles.OpenAsync(storage, key, cancellationToken);
    }

    private async Task<Result<CooperativeProfileView>> SetAsync(Cooperative cooperative, string? key, CancellationToken cancellationToken)
    {
        var replaced = cooperative.CoverPhotoUrl;
        audit.Record(new AuditEntry("Cooperative.CoverPhotoChanged", nameof(Cooperative))
        {
            RecordId = cooperative.Id,
            Before = new { CoverPhotoUrl = replaced },
            After = new { CoverPhotoUrl = key },
        });
        cooperative.CoverPhotoUrl = key;
        await fileSaver.SaveAsync(key, replaced, cancellationToken);
        return await views.ProfileAsync(cooperative, cancellationToken);
    }
}
