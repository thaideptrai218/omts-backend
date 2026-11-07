using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>Stores the photos of one save, all or none (GFR-01): when one cannot be uploaded the ones before it are removed, and nothing is saved (MSG-GEN-03).</summary>
public sealed class CultivationLogPhotoStore(IFileStorage storage, UploadedFileSaver fileSaver)
{
    public async Task<Result<IReadOnlyList<string>>> UploadAllAsync(IReadOnlyList<ValidatedFile> files, CancellationToken cancellationToken)
    {
        var keys = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var uploaded = await storage.UploadAsync(file, FileCategories.CultivationLogPhotos, cancellationToken);
                if (uploaded is not { Succeeded: true, Key: { } key })
                {
                    await fileSaver.DiscardAsync(keys);
                    return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
                }

                keys.Add(key);
            }
        }
        catch
        {
            // The request was cancelled, or the provider broke its promise not to throw.
            await fileSaver.DiscardAsync(keys);
            throw;
        }

        return keys;
    }

    /// <summary>
    /// Runs what comes between the uploads and the save. A refusal or a failure there, or the client giving up, removes the uploads of
    /// <paramref name="keys"/>: no row will point to them, so they need not wait for the orphan file clean-up.
    /// </summary>
    public async Task<Result<T>> PrepareSaveAsync<T>(IReadOnlyList<string> keys, Func<Task<Result<T>>> prepare)
    {
        try
        {
            var prepared = await prepare();
            if (!prepared.IsSuccess)
            {
                await fileSaver.DiscardAsync(keys);
            }

            return prepared;
        }
        catch
        {
            await fileSaver.DiscardAsync(keys);
            throw;
        }
    }
}
