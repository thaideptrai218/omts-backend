using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// The meter photo of a measurement (BR-WATER-02). It is stored at recognition, before any measurement exists, under a
/// folder of the uploading account, and that key is the proof the record request brings back: a key of another
/// account, one already used, or one that was never stored (or cleaned up as an orphan) is refused.
/// </summary>
public sealed class WaterMeterPhotoStore(IFileStorage storage, IWaterMeasurementRepository measurements)
{
    /// <summary>A stored key is about 100 characters; the column holds <see cref="MaxKeyLength"/>.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The checked photo and its storage key, or the refusal of the request.</summary>
    public async Task<Result<(ValidatedFile File, string Key)>> StoreAsync(IncomingFile? photo, Guid accountId, CancellationToken cancellationToken)
    {
        if (photo is null)
        {
            return AppError.Invalid(WaterMeasurementRecordingService.PhotoField);
        }

        var checkedPhoto = FileValidator.Validate([photo], FileRules.Photos);
        if (!checkedPhoto.IsValid)
        {
            return AppError.Invalid(WaterMeasurementRecordingService.PhotoField, checkedPhoto.Problem);
        }

        // Stored at original quality: it is the proof of the values.
        var uploaded = await storage.UploadAsync(checkedPhoto.Files[0], OwnerFolder(accountId), cancellationToken);
        if (uploaded is not { Succeeded: true, Key: { } key })
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        return (checkedPhoto.Files[0], key);
    }

    /// <summary>Whether <paramref name="key"/> is a stored photo of this account that no measurement uses yet.</summary>
    public async Task<bool> IsUsableAsync(string? key, Guid accountId, CancellationToken cancellationToken)
    {
        if (key is null || key.Length > MaxKeyLength || !key.StartsWith($"{OwnerFolder(accountId)}/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (await measurements.IsPhotoUsedAsync(key, cancellationToken))
        {
            return false;
        }

        await using var stored = await storage.OpenReadAsync(key, cancellationToken);
        return stored is not null;
    }

    private static string OwnerFolder(Guid accountId) => $"{FileCategories.WaterMeterPhotos}/{accountId:N}";
}
