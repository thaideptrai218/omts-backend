using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The checked fields of a log (BR-CYCLE-05; MSG-GEN-01 names the others), and the values the audit log keeps.</summary>
internal sealed record CultivationLogValues(DateOnly VisitDate, string? Notes, IReadOnlyList<ValidatedFile> NewPhotos)
{
    private const string PhotosField = "Photos";
    private const int MaxNotesLength = 2000;
    private const int MaxReasonLength = 1000;

    /// <summary>Checks the fields; a null answer means <paramref name="values"/> can be saved.</summary>
    /// <param name="kept">How many current photos stay on the log.</param>
    /// <param name="keysAreKnown">Every key the client asked to keep is a photo of this log.</param>
    /// <param name="reason">The reason of a correction or removal; not read when <paramref name="reasonRequired"/> is false.</param>
    public static AppError? Read(
        DateOnly? visitDate, string? notes, IReadOnlyList<IncomingFile> newPhotos, int kept, bool keysAreKnown, string? reason, bool reasonRequired,
        DateOnly cycleStart, DateOnly today, out CultivationLogValues values)
    {
        var check = new FieldCheck();
        check.Check(visitDate is { } visit && visit >= cycleStart && visit <= today, "Visit date");
        var checkedNotes = check.Optional(notes, "Notes", MaxNotesLength);
        var (files, photoHint) = CheckPhotos(newPhotos, kept, keysAreKnown);
        check.Check(photoHint is null, PhotosField);
        if (reasonRequired)
        {
            CheckReason(check, reason);
        }

        values = new CultivationLogValues(visitDate ?? default, checkedNotes, files);
        return check.IsValid
            ? null
            : check.Invalid is [PhotosField] ? AppError.Invalid(PhotosField, photoHint) : AppError.InvalidFields(check.Invalid);
    }

    /// <summary>A reason of 1 to 1000 characters (BR-CYCLE-05).</summary>
    public static void CheckReason(FieldCheck check, string? reason) => check.Required(reason, "Reason", MaxReasonLength);

    public static object Snapshot(CultivationLog log) => new
    {
        log.VisitDate,
        log.Notes,
        Photos = log.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList(),
    };

    private static (IReadOnlyList<ValidatedFile> Files, string? Hint) CheckPhotos(IReadOnlyList<IncomingFile> files, int kept, bool keysAreKnown)
    {
        if (!keysAreKnown)
        {
            return ([], "Keep only photos of this log.");
        }

        if (kept + files.Count == 0)
        {
            return ([], "Attach at least one photo.");
        }

        if (kept + files.Count > CultivationLog.MaxPhotos)
        {
            return ([], $"A log holds at most {CultivationLog.MaxPhotos} photos.");
        }

        var validated = FileValidator.Validate(files, FileRules.Photos);
        return validated.IsValid ? (validated.Files, null) : ([], validated.Problem);
    }
}
