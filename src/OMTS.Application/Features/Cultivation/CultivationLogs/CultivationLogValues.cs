using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The checked fields of a log (BR-CYCLE-05; MSG-GEN-01 names the others), and the values the audit log keeps.</summary>
/// <remarks><see cref="LogType"/> is null when a correction named none: the log keeps the type it has.</remarks>
internal sealed record CultivationLogValues(DateOnly VisitDate, string? LogType, string? Notes, IReadOnlyList<ValidatedFile> NewPhotos)
{
    private const string PhotosField = "Photos";
    private const string TypeField = "LogType";
    private const int MaxNotesLength = 2000;
    private const int MaxPhotosPerLog = StepPhotoService.MaxPhotos;
    private const int MaxReasonLength = 1000;
    private const string UnknownKeysHint = "Keep only photos of this log.";

    /// <summary>Checks the fields; a null answer means <paramref name="values"/> can be saved.</summary>
    /// <param name="logType">
    /// A value that is no <see cref="CultivationLogTypes"/> code is invalid. Missing or blank is <c>OTHER</c> on a new log; on a correction (<paramref name="reasonRequired"/>)
    /// it keeps the current type, as the apps that do not send it yet must not reset it.
    /// </param>
    /// <param name="otherFilePart">A file came in a part not named <c>photos</c>.</param>
    /// <param name="kept">How many current photos stay on the log.</param>
    /// <param name="keysAreKnown">Every key the client asked to keep is a photo of this log.</param>
    /// <param name="earliest">The first day the visit may have: the start date of the cycle a log is recorded in; null when the log is worked out from the date and has no lower bound.</param>
    /// <param name="latest">The last day the visit may have: today.</param>
    /// <param name="reason">The reason of a correction or removal; not read when <paramref name="reasonRequired"/> is false.</param>
    public static AppError? Read(
        DateOnly? visitDate, string? logType, string? notes, IReadOnlyList<IncomingFile> newPhotos, bool otherFilePart, int kept, bool keysAreKnown, string? reason, bool reasonRequired,
        DateOnly? earliest, DateOnly latest, out CultivationLogValues values)
    {
        var check = new FieldCheck();
        check.Check(visitDate is { } visit && (earliest is null || visit >= earliest) && visit <= latest, "Visit date");
        var normalizedType = CultivationLogTypes.Normalize(logType);
        check.Check(normalizedType is not null, TypeField);
        var keepsType = reasonRequired && string.IsNullOrWhiteSpace(logType);
        var checkedNotes = check.Optional(notes, "Notes", MaxNotesLength);
        var (files, photoHint) = otherFilePart ? ([], "Send each photo in a part named photos.") : CheckPhotos(newPhotos, kept, keysAreKnown);
        check.Check(photoHint is null, PhotosField);
        if (reasonRequired)
        {
            CheckReason(check, reason);
        }

        values = new CultivationLogValues(visitDate ?? default, keepsType ? null : normalizedType ?? CultivationLogTypes.Other, checkedNotes, files);
        return check.IsValid
            ? null
            : check.Invalid is [PhotosField] ? AppError.Invalid(PhotosField, photoHint) : AppError.InvalidFields(check.Invalid);
    }

    /// <summary>
    /// After the uploads, against the row as it now is: what another save can have changed since <see cref="Read"/>. The start date may have moved past the
    /// visit, and a photo to keep may be gone with its file, so that request cannot be honoured. The cap still holds, as the kept photos can only be the same ones.
    /// </summary>
    public static AppError? ReadAgain(DateOnly visitDate, DateOnly? earliest, bool keysAreKnown = true)
    {
        if (visitDate < earliest)
        {
            return AppError.InvalidFields(["Visit date"]);
        }

        return keysAreKnown ? null : AppError.Invalid(PhotosField, UnknownKeysHint);
    }

    /// <summary>
    /// The <c>logType</c> filter of the list: no filter (null) when absent or blank; an unknown value is MSG-GEN-01 on <c>LogType</c>, after the access check like the other fields.
    /// </summary>
    public static AppError? ReadTypeFilter(string? logType, out string? wanted)
    {
        wanted = null;
        if (string.IsNullOrWhiteSpace(logType))
        {
            return null;
        }

        wanted = CultivationLogTypes.Normalize(logType);
        return wanted is null ? AppError.Invalid(TypeField) : null;
    }

    /// <summary>A reason of 1 to 1000 characters (BR-CYCLE-05).</summary>
    public static void CheckReason(FieldCheck check, string? reason) => check.Required(reason, "Reason", MaxReasonLength);

    public static object Snapshot(CultivationLog log) => new
    {
        log.VisitDate,
        log.LogType,
        log.Notes,
        Photos = log.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList(),
    };

    private static (IReadOnlyList<ValidatedFile> Files, string? Hint) CheckPhotos(IReadOnlyList<IncomingFile> files, int kept, bool keysAreKnown)
    {
        if (!keysAreKnown)
        {
            return ([], UnknownKeysHint);
        }

        // The photos of a care log are optional, at most three (owner decision of 12/11/2026).
        if (kept + files.Count > MaxPhotosPerLog)
        {
            return ([], $"A log holds at most {MaxPhotosPerLog} photos.");
        }

        var validated = FileValidator.Validate(files, FileRules.Photos with { MaxFiles = MaxPhotosPerLog });
        return validated.IsValid ? (validated.Files, null) : ([], validated.Problem);
    }
}
