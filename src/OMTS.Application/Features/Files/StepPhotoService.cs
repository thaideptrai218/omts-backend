using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Files;

/// <summary>A photo of a step as the client reads it: <paramref name="Id"/> is the photo row's id, <paramref name="Url"/> a short-lived signed read link.</summary>
public sealed record StepPhotoView(Guid Id, string Key, string Url);

/// <summary>
/// The photos of the important farming steps (nursery start and finish, cycle plan, seeding, harvest, lane incident), one shared rule (owner decision of 12/11/2026):
/// <see cref="MinPhotos"/> to <see cref="MaxPhotos"/> photos per record, JPEG or PNG read from the content, at most 10 MB each. A record that only
/// allows photos (the care log) passes <c>minimum: 0</c>. Files always go through <see cref="IFileStorage"/>, so a change of <c>Storage:Provider</c> moves them
/// to the cloud without code changes; this service never touches a disk path.
/// </summary>
/// <remarks>
/// Edit flow: the caller counts the photos it keeps (<c>kept</c>), sends the new ones through <see cref="StoreAsync"/>, saves its rows with
/// <see cref="UploadedFileSaver.SaveAsync(IReadOnlyCollection{string}, IReadOnlyCollection{string}, CancellationToken, Func{Exception, bool}?)"/>
/// (new keys as uploads, removed keys as replaced), so the removed files go only after the save commits and the new ones go when it fails.
/// The key is <c>{category}/{yyyy}/{MM}/{guid}{ext}</c> (one category per photo table, see <see cref="FileCategories"/>): the cooperative and the record are
/// known from the photo row, not from the key, so the orphan file clean-up keeps working.
/// </remarks>
public sealed class StepPhotoService(IFileStorage storage, RecordPhotoStore photoStore)
{
    public const int MinPhotos = 1;
    public const int MaxPhotos = 3;
    public const string Field = "Photos";

    /// <summary>Checks the photo count and the files without storing anything.</summary>
    /// <param name="newPhotos">Photos sent with this save.</param>
    /// <param name="kept">Photos of the record that stay (0 when creating).</param>
    /// <param name="otherFilePart">The request carried a file part not named <c>photos</c>.</param>
    /// <param name="keysAreKnown">Every photo the client asked to keep belongs to this record.</param>
    /// <param name="minimum">The least photos the record must end with.</param>
    public static Result<IReadOnlyList<ValidatedFile>> Read(
        IReadOnlyList<IncomingFile> newPhotos, int kept = 0, bool otherFilePart = false, bool keysAreKnown = true, int minimum = MinPhotos)
    {
        if (otherFilePart)
        {
            return AppError.Invalid(Field, "Gửi mỗi ảnh trong một phần tên photos.");
        }

        if (!keysAreKnown)
        {
            return AppError.Invalid(Field, "Chỉ giữ lại ảnh của chính bản ghi này.");
        }

        var total = kept + newPhotos.Count;
        if (total < minimum)
        {
            return AppError.Invalid(Field, $"Cần ít nhất {minimum} ảnh (tối đa {MaxPhotos} ảnh).");
        }

        if (total > MaxPhotos)
        {
            return AppError.Invalid(Field, $"Tối đa {MaxPhotos} ảnh cho mỗi bước.");
        }

        var validated = FileValidator.Validate(newPhotos, FileRules.Photos with { MaxFiles = MaxPhotos });
        return validated.IsValid
            ? Result<IReadOnlyList<ValidatedFile>>.Success(validated.Files)
            : AppError.Invalid(Field, "Ảnh phải là JPEG hoặc PNG, mỗi ảnh tối đa 10 MB.");
    }

    /// <summary>
    /// Checks then stores the photos, all or none (a failed upload removes the ones before it, MSG-GEN-03). The returned ids and keys are what the caller saves in its photo rows (the id is new, the url a signed read link);
    /// when its own checks then refuse the save it passes them to <see cref="RecordPhotoStore.PrepareSaveAsync{T}"/>, or discards them.
    /// </summary>
    /// <param name="category">The photo table's category in <see cref="FileCategories"/>.</param>
    public async Task<Result<IReadOnlyList<StepPhotoView>>> StoreAsync(
        string category, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken,
        int kept = 0, bool otherFilePart = false, bool keysAreKnown = true, int minimum = MinPhotos)
    {
        var read = Read(newPhotos, kept, otherFilePart, keysAreKnown, minimum);
        if (!read.IsSuccess)
        {
            return read.Error!;
        }

        if (read.Value!.Count == 0)
        {
            return Result<IReadOnlyList<StepPhotoView>>.Success([]);
        }

        var uploaded = await photoStore.UploadAllAsync(category, read.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        try
        {
            return Result<IReadOnlyList<StepPhotoView>>.Success(await ViewAllAsync(uploaded.Value!.Select(key => (Guid.NewGuid(), key)), cancellationToken));
        }
        catch
        {
            // A link that cannot be made leaves no row pointing to the uploads: remove them rather than wait for the clean-up.
            foreach (var key in uploaded.Value!)
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>The photo as the client reads it; the id is the one of the photo row.</summary>
    public async Task<StepPhotoView> ViewAsync(Guid id, string key, CancellationToken cancellationToken) =>
        new(id, key, await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<StepPhotoView>> ViewAllAsync(IEnumerable<(Guid Id, string Key)> photos, CancellationToken cancellationToken)
    {
        var views = new List<StepPhotoView>();
        foreach (var (id, key) in photos)
        {
            views.Add(await ViewAsync(id, key, cancellationToken));
        }

        return views;
    }
}
