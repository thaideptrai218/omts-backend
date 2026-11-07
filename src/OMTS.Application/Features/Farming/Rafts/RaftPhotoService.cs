using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 raft photos, Cooperative Owner only (API filter): up to <see cref="Raft.MaxPhotos"/>, JPEG or PNG of at most
/// 10 MB, stored unchanged and added or removed one at a time. A storage failure saves nothing (MSG-GEN-03, GFR-01), and
/// <see cref="UploadedFileSaver"/> leaves no file behind. Each change also saves the raft: its row version moves, and a
/// parallel change of the same raft fails with MSG06.
/// </summary>
public sealed class RaftPhotoService(
    IRaftRepository rafts,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    RaftViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<RaftSaved>> AddAsync(Guid raftId, IncomingFile file, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (views.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        if (raft.Photos.Count >= Raft.MaxPhotos)
        {
            return AppError.Invalid("Photos", $"A raft holds at most {Raft.MaxPhotos} photos. Remove one first.");
        }

        var validated = FileValidator.Validate([file], FileRules.Photos with { MaxFiles = 1 });
        if (!validated.IsValid)
        {
            return AppError.Invalid("Photo");
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], FileCategories.RaftPhotos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var before = Keys(raft);
        var photo = new RaftPhoto { RaftId = raft.Id, SortOrder = FreeSlot(raft), FileKey = uploaded.Key! };
        raft.Photos.Add(photo);
        rafts.AddPhoto(photo);
        Record(raft, before);
        try
        {
            await fileSaver.SaveAsync(uploaded.Key, null, cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            // A parallel upload took the slot: the same answer as when it changed the raft first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }

        return await views.SavedAsync(raft, "Raft photo", cancellationToken);
    }

    public async Task<Result<RaftSaved>> RemoveAsync(Guid raftId, Guid photoId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null || raft.Photos.FirstOrDefault(p => p.Id == photoId) is not { } photo)
        {
            return AppError.NotFound;
        }

        if (views.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        var before = Keys(raft);
        raft.Photos.Remove(photo);
        rafts.RemovePhoto(photo);
        Record(raft, before);
        await fileSaver.SaveAsync(null, photo.FileKey, cancellationToken);
        return await views.SavedAsync(raft, "Raft", cancellationToken);
    }

    private static int FreeSlot(Raft raft) =>
        Enumerable.Range(1, Raft.MaxPhotos).First(slot => raft.Photos.All(p => p.SortOrder != slot));

    private static List<string> Keys(Raft raft) => raft.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList();

    private void Record(Raft raft, List<string> before)
    {
        unitOfWork.MarkChanged(raft);
        audit.Record(new AuditEntry("Raft.PhotosChanged", nameof(Raft))
        {
            RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, Before = new { Photos = before }, After = new { Photos = Keys(raft) },
        });
    }
}
