namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>CYCLE-04.0; the photos come beside it.</summary>
public sealed record RecordCultivationLogRequest(DateOnly? VisitDate, string? Notes);

/// <summary>CYCLE-04.1: the log as it should be, with the reason. A photo that is not kept and not sent again is removed.</summary>
/// <param name="KeepPhotoKeys">The <c>key</c> of each current photo to keep, spelled as the log view gives it.</param>
public sealed record CorrectCultivationLogRequest(DateOnly? VisitDate, string? Notes, IReadOnlyList<string?>? KeepPhotoKeys, string? Reason, string? RowVersion);

/// <summary>CYCLE-04.1 remove.</summary>
public sealed record RemoveCultivationLogRequest(string? Reason, string? RowVersion);

/// <summary>A photo of a log, for staff only (BR-FARM-09).</summary>
/// <param name="Key">The storage key: send it in <c>keepPhotoKeys</c> to keep the photo on a correction.</param>
/// <param name="Url">A signed link that expires; read the log again for a new one.</param>
public sealed record CultivationLogPhotoView(string Key, int SortOrder, string Url);

/// <summary>A cultivation log (CYCLE-04, CYCLE-05).</summary>
/// <param name="Photos">In slot order.</param>
/// <param name="CanCorrect">The caller may correct or remove it now: the cycle is Open on a raft In Use, and the caller recorded it or is the Owner (BR-CYCLE-05).</param>
public sealed record CultivationLogView(
    Guid Id,
    Guid CycleId,
    Guid RaftId,
    string RaftCode,
    DateOnly VisitDate,
    string? Notes,
    IReadOnlyList<CultivationLogPhotoView> Photos,
    Guid RecordedByAccountId,
    string? RecordedByName,
    DateTimeOffset RecordedAt,
    bool CanCorrect,
    string RowVersion);

/// <summary>Answer of a saved log.</summary>
/// <param name="Message">MSG-GEN-05 "Cultivation log saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CultivationLogSaved(CultivationLogView Log, string Message, string MessageCode);
