namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>CYCLE-04.0, in a cycle or on a raft; the photos come beside it.</summary>
/// <param name="LogType">One of <c>FEEDING</c>, <c>CLEANING</c>, <c>INSPECTION</c>, <c>DISEASE</c>, <c>REPAIR</c>, <c>OTHER</c>; missing or blank is <c>OTHER</c>, any other value is refused.</param>
/// <param name="OtherFilePart">A file came in a part not named <c>photos</c>: refused after the access check, so no photo is dropped without a word.</param>
public sealed record RecordCultivationLogRequest(DateOnly? VisitDate, string? Notes, bool OtherFilePart = false, string? LogType = null);

/// <summary>CYCLE-04.1: the log as it should be, with the reason. A photo that is not kept and not sent again is removed. It carries no version (NFR-09).</summary>
/// <param name="KeepPhotoKeys">The <c>key</c> of each current photo to keep, spelled as the log view gives it.</param>
/// <param name="OtherFilePart">As on <see cref="RecordCultivationLogRequest"/>.</param>
/// <param name="LogType">As on <see cref="RecordCultivationLogRequest"/>; a correction that sends none keeps the current type.</param>
public sealed record CorrectCultivationLogRequest(
    DateOnly? VisitDate, string? Notes, IReadOnlyList<string?>? KeepPhotoKeys, string? Reason, bool OtherFilePart = false, string? LogType = null);

/// <summary>CYCLE-04.1 remove. It carries no version (NFR-09).</summary>
public sealed record RemoveCultivationLogRequest(string? Reason);

/// <summary>A photo of a log, for staff only (BR-FARM-09).</summary>
/// <param name="Key">The storage key: send it in <c>keepPhotoKeys</c> to keep the photo on a correction.</param>
/// <param name="Url">A signed link that expires; read the log again for a new one.</param>
public sealed record CultivationLogPhotoView(string Key, int SortOrder, string Url);

/// <summary>A cultivation log (CYCLE-04, CYCLE-05).</summary>
/// <param name="LogType">A code of <see cref="OMTS.Domain.Constants.CultivationLogTypes"/>.</param>
/// <param name="CycleId">The Open cycle of the raft that the visit date falls in; null for a visit in no cycle.</param>
/// <param name="CycleStartDate">The start date of that cycle; null with <paramref name="CycleId"/>.</param>
/// <param name="Photos">In slot order.</param>
/// <param name="CanCorrect">The caller may correct or remove it now: its cycle, if any, is Open, the raft is In Use or Discontinued in a zone In Use, and the caller recorded it or is the Owner (BR-CYCLE-05).</param>
public sealed record CultivationLogView(
    Guid Id,
    Guid? CycleId,
    DateOnly? CycleStartDate,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    DateOnly VisitDate,
    string LogType,
    string? Notes,
    IReadOnlyList<CultivationLogPhotoView> Photos,
    Guid RecordedByAccountId,
    string? RecordedByName,
    DateTimeOffset RecordedAt,
    bool CanCorrect,
    string RowVersion);

/// <summary>The query of the cross-raft log list (the "Nhật ký" screen). <c>from</c> and <c>to</c> are visit dates, inclusive.</summary>
/// <param name="ZoneId">Only the logs of rafts of this zone; none asks for every zone the caller may read.</param>
/// <param name="RaftId">Only the logs of this raft, in every cycle it had and in none.</param>
/// <param name="LogType">One code of <see cref="OMTS.Domain.Constants.CultivationLogTypes"/>; blank is any.</param>
/// <param name="Search">Text in the raft code or the notes.</param>
/// <param name="SortBy"><c>visitDate</c> (the default), <c>zone</c>, <c>raft</c>, <c>logType</c>, <c>recordedBy</c> or <c>recordedAt</c>, any letter case; another value is MSG-GEN-01 on <c>SortBy</c>.</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (the default), any letter case; another value is MSG-GEN-01 on <c>SortDir</c>.</param>
/// <param name="PageSize">1 to 100.</param>
public sealed record CultivationLogListQuery(
    Guid? ZoneId = null, Guid? RaftId = null, string? LogType = null, DateOnly? From = null, DateOnly? To = null, string? Search = null,
    string? SortBy = null, string? SortDir = null, int Page = 1, int PageSize = 20);

/// <summary>A page of the cross-raft log list, the latest visit first.</summary>
public sealed record CultivationLogPage(IReadOnlyList<CultivationLogView> Items, int Page, int PageSize, int Total);

/// <summary>Answer of a saved log.</summary>
/// <param name="Message">MSG-GEN-05 "Cultivation log saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CultivationLogSaved(CultivationLogView Log, string Message, string MessageCode);
