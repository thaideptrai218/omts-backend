using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The optional photo of a lane incident (decision of 12/11/2026): JPEG or PNG of at most 10 MB, at most <see cref="LaneIncident.MaxPhotos"/> per incident, added one at a time after the incident
/// is saved. For the Owner and a Zone Manager of the raft's zone. A storage failure saves nothing (MSG-GEN-03) and leaves no file behind.
/// </summary>
public sealed class LaneIncidentPhotoService(
    IRaftRepository rafts,
    ILaneIncidentRepository incidents,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    LaneIncidentViews views,
    RaftViews raftViews,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit)
{
    public async Task<Result<LaneIncidentView>> AddAsync(Guid raftId, int laneNumber, Guid incidentId, IncomingFile file, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        var incident = await incidents.GetForEditAsync(incidentId, cancellationToken);
        if (raft is null || incident is null || incident.Lane.RaftId != raft.Id || incident.Lane.LaneNumber != laneNumber)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (raftViews.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        if (incident.Photos.Count >= LaneIncident.MaxPhotos)
        {
            return AppError.Invalid("Photos", $"An incident holds at most {LaneIncident.MaxPhotos} photos.");
        }

        var validated = FileValidator.Validate([file], FileRules.Photos with { MaxFiles = 1 });
        if (!validated.IsValid)
        {
            return AppError.Invalid("Photo");
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], FileCategories.LaneIncidentPhotos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.UploadFailed);
        }

        var slot = Enumerable.Range(1, LaneIncident.MaxPhotos).First(s => incident.Photos.All(p => p.SortOrder != s));
        var photo = new LaneIncidentPhoto { IncidentId = incident.Id, SortOrder = slot, FileKey = uploaded.Key! };
        incident.Photos.Add(photo);
        incidents.AddPhoto(photo);
        audit.Record(new AuditEntry("Lane.IncidentPhotoAdded", LaneIncidentAudit.RecordType)
        {
            RecordId = incident.LaneId, RecordCode = $"{raft.Code}/{laneNumber}", ZoneId = raft.ZoneId, After = new { IncidentId = incident.Id, Photo = photo.FileKey },
        });
        try
        {
            await fileSaver.SaveAsync(uploaded.Key, null, cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            // A parallel upload took the slot: the same answer as when it changed the incident first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }

        return await views.ViewAsync(incident, cancellationToken);
    }
}
