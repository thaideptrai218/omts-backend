using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor;

/// <param name="BreakStart"><c>HH:mm</c>; both times, or neither for no break.</param>
/// <param name="BreakEnd"><c>HH:mm</c>, after the start.</param>
/// <param name="EffectiveFrom">After the last day of every pay period whose payroll is Awaiting finalization or Finalized.</param>
/// <param name="RowVersion">The version the screen was read from.</param>
public sealed record SaveAttendanceSettingsRequest(
    string? BreakStart, string? BreakEnd, AttendanceRoundingUnit? RoundingUnit, DateOnly? EffectiveFrom, string? RowVersion);

/// <param name="EffectiveFrom">Null only for the settings when OMTS is set up: no break and no rounding.</param>
public sealed record AttendanceSettingsView(TimeOnly? BreakStart, TimeOnly? BreakEnd, AttendanceRoundingUnit RoundingUnit, DateOnly? EffectiveFrom);

/// <param name="Current">The settings in force today.</param>
/// <param name="Versions">Every saved version, the latest date first, also one still to come.</param>
public sealed record AttendanceSettingsOverview(AttendanceSettingsView Current, IReadOnlyList<AttendanceSettingsView> Versions, string RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record AttendanceSettingsSaved(AttendanceSettingsOverview Settings, string Message, string MessageCode);

/// <summary>
/// LABOR-15: the Cooperative Owner sets the break and the rounding unit from a date; the earlier settings stay. The history has
/// no one row to version, so the settings go by the row of the cooperative they belong to (§I.3.2): each save moves it, and
/// parallel saves answer MSG06. The client asks MSG-LABOR-35 before it sends.
/// </summary>
public sealed class AttendanceSettingsService(
    IAttendanceSettingsRepository versions,
    ICooperativeRepository cooperatives,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private static readonly AttendanceSettingsView SetUp = new(null, null, AttendanceRoundingUnit.None, null);
    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss"];

    public async Task<Result<AttendanceSettingsOverview>> GetAsync(CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative
            ? Overview(await versions.ListAsync(cancellationToken), cooperative)
            : AppError.NotFound;

    public async Task<Result<AttendanceSettingsSaved>> SaveAsync(SaveAttendanceSettingsRequest request, CancellationToken cancellationToken)
    {
        // The history before the cooperative: a save that lands between them shows as a stale version, MSG06. The other way round
        // the version check passes first, and this save would then find that save's row, "change nothing" and hand back the old version.
        var all = await versions.ListAsync(cancellationToken);
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var breakStart = Time(check, request.BreakStart, "Break start");
        var breakEnd = Time(check, request.BreakEnd, "Break end");
        if (check.IsValid)
        {
            check.Check(breakStart is not null || breakEnd is null, "Break start");
            check.Check(breakStart is null || breakEnd > breakStart, "Break end");
        }

        check.Check(request.RoundingUnit is { } unit && Enum.IsDefined(unit), "Rounding unit");
        var from = JobTypeFields.EffectiveDate(check, request.EffectiveFrom);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cooperative, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (await payPeriods.CheckAsync(from, cancellationToken) is { } locked)
        {
            return locked;
        }

        var sameDate = all.FirstOrDefault(v => v.EffectiveFrom == from);
        if (sameDate is not null && sameDate.BreakStart == breakStart && sameDate.BreakEnd == breakEnd && sameDate.RoundingUnit == request.RoundingUnit)
        {
            return Saved(all, cooperative);
        }

        var before = sameDate is null ? null : Snapshot(sameDate);
        var version = sameDate ?? new AttendanceSettingsVersion { EffectiveFrom = from };
        version.BreakStart = breakStart;
        version.BreakEnd = breakEnd;
        version.RoundingUnit = request.RoundingUnit!.Value;
        version.EnteredByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Attendance settings are saved by a logged-in Owner.");
        if (sameDate is null)
        {
            versions.Add(version);
        }

        audit.Record(new AuditEntry("AttendanceSettings.Changed", nameof(AttendanceSettingsVersion)) { RecordId = version.Id, Before = before, After = Snapshot(version) });
        unitOfWork.MarkChanged(cooperative);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save of one date: the insert meets its index before the version check, which would answer the same.
            return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }

        return Saved(await versions.ListAsync(cancellationToken), cooperative);
    }

    private AttendanceSettingsSaved Saved(IReadOnlyList<AttendanceSettingsVersion> all, Cooperative cooperative)
    {
        var saved = UserMessages.Saved("Attendance settings");
        return new AttendanceSettingsSaved(Overview(all, cooperative), saved.Text, saved.Code);
    }

    private AttendanceSettingsOverview Overview(IReadOnlyList<AttendanceSettingsVersion> all, Cooperative cooperative)
    {
        var current = AttendanceSettingsVersion.EffectiveOn(all, timeProvider.Today());
        return new AttendanceSettingsOverview(current is null ? SetUp : View(current), all.Select(View).ToList(), RowVersionGuard.Encode(cooperative.RowVersion));
    }

    /// <summary>Blank is no time; the view's own <c>HH:mm:ss</c> may be sent back as it is.</summary>
    private static TimeOnly? Time(FieldCheck check, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var readable = TimeOnly.TryParseExact(value.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
        check.Check(readable, field);
        return readable ? time : null;
    }

    private static AttendanceSettingsView View(AttendanceSettingsVersion v) => new(v.BreakStart, v.BreakEnd, v.RoundingUnit, v.EffectiveFrom);

    private static object Snapshot(AttendanceSettingsVersion v) => new { v.BreakStart, v.BreakEnd, v.RoundingUnit, v.EffectiveFrom };
}
