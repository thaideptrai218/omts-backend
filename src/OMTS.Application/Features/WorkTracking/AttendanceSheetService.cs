using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.WorkTracking.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkTracking;

/// <summary>
/// LAB-04 – saves the attendance sheet of a zone and day in one transaction: one invalid row and nothing is saved (errors
/// per row). One record per person per day across all zones; changing a saved record needs a reason (kept in the audit).
/// </summary>
public sealed class AttendanceSheetService(
    IAttendanceRepository attendance,
    IWorkAssignmentRepository assignments,
    AttendanceRowExpander expander,
    AttendanceSheetQuery sheets,
    LaborEvidence evidence,
    PayrollPeriodLock periodLock,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string EvidenceCategory = "attendance-evidence";

    public async Task<Result<AttendanceSheet>> SaveAsync(Guid zoneId, DateOnly date, SaveAttendanceRequest request, CancellationToken cancellationToken)
    {
        var zone = await assignments.GetZoneAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Invalid("Zone");
        }

        if (date > timeProvider.Today())
        {
            return AppError.Invalid("Work date");
        }

        if (request.Rows is not { Count: > 0 } rows)
        {
            return AppError.Invalid("Rows");
        }

        if (await periodLock.CheckAsync(date, cancellationToken) is { } locked)
        {
            return locked;
        }

        var (entries, errors) = await expander.ExpandAsync(rows, date, cancellationToken);
        var existing = await attendance.ListForPeopleAsync(
            date, entries.Where(e => e.Employee is not null).Select(e => e.Employee!.Id).ToList(),
            entries.Where(e => e.Worker is not null).Select(e => e.Worker!.Id).ToList(), cancellationToken);
        var changes = new List<(AttendanceEntry Entry, AttendanceRecord? Record)>();
        foreach (var entry in entries)
        {
            var record = existing.FirstOrDefault(r => (r.EmployeeId ?? r.WorkerId) == (entry.Employee?.Id ?? entry.Worker!.Id));
            var problem = record switch
            {
                not null when record.ZoneId != zoneId => AppError.AlreadyExists($"Attendance of {entry.Name} on {LaborInputRules.Date(date)}", record.Id),
                null when entry.Worker is { StatusId: not StatusIds.Usage.InUse } => AppError.Invalid("Worker"),
                not null when Changed(record, entry) && entry.Reason is null => AppError.Invalid("Reason", "Changing a saved attendance record needs a reason."),
                _ => null,
            };
            if (problem is not null)
            {
                errors.Add(AttendanceRowExpander.Error(entry.Index, entry.Name, problem));
            }
            else if (record is null || Changed(record, entry))
            {
                changes.Add((entry, record));
            }
        }

        if (errors.Count > 0)
        {
            var ordered = errors.OrderBy(e => e.Index).ToList();
            var first = ordered[0];
            var kind = first.Code switch { "MSG01" => ErrorKind.Validation, _ => ErrorKind.Conflict };
            var details = new Dictionary<string, object?> { ["rows"] = ordered };
            if (first.Field is not null)
            {
                details["field"] = first.Field;
            }

            return new AppError(kind, new UserMessage(first.Code, first.Message), details);
        }

        if (changes.Any(c => c.Record is not null)
            && !await zoneAccess.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Edit, zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        foreach (var (entry, record) in changes)
        {
            Apply(zoneId, date, entry, record);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel save recorded one of these people first (one record per person per day).
            return new AppError(ErrorKind.Conflict, UserMessages.AlreadyExists($"Attendance on {LaborInputRules.Date(date)}"));
        }

        return await sheets.GetAsync(zoneId, date, cancellationToken);
    }

    /// <summary>Adds evidence photos (up to 5 in all) to a saved record of an open month.</summary>
    public async Task<Result<AttendanceSheetRow>> AddEvidenceAsync(Guid zoneId, Guid recordId, IReadOnlyList<IncomingFile> files, CancellationToken cancellationToken)
    {
        var record = await attendance.GetAsync(recordId, cancellationToken);
        if (record is null || record.ZoneId != zoneId)
        {
            return AppError.NotFound;
        }

        if (await periodLock.CheckAsync(record.WorkDate, cancellationToken) is { } locked)
        {
            return locked;
        }

        var stored = await evidence.UploadAsync(files, record.EvidenceUrls.Count, EvidenceCategory, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        record.EvidenceUrls = [.. record.EvidenceUrls, .. stored.Value!];
        audit.Record(new AuditEntry("AttendanceRecord.EvidenceAdded", nameof(AttendanceRecord))
        {
            RecordId = record.Id, ZoneId = zoneId, After = new { Added = stored.Value!.Count, Total = record.EvidenceUrls.Count },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = (await attendance.ListForZoneAsync(zoneId, record.WorkDate, cancellationToken)).First(r => r.Id == record.Id);
        return await sheets.RowAsync(saved, cancellationToken);
    }

    private void Apply(Guid zoneId, DateOnly date, AttendanceEntry entry, AttendanceRecord? record)
    {
        var before = record is null ? null : Snapshot(record);
        if (record is null)
        {
            record = new AttendanceRecord
            {
                ZoneId = zoneId, WorkDate = date, EmployeeId = entry.Employee?.Id, WorkerId = entry.Worker?.Id,
                RecordedByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Attendance needs an authenticated caller."),
            };
            attendance.Add(record);
        }

        (record.Result, record.Hours, record.Notes, record.ContractorId) = (entry.Result, entry.Hours, entry.Notes, entry.TeamOfDayId);
        audit.Record(new AuditEntry(before is null ? "AttendanceRecord.Created" : "AttendanceRecord.Updated", nameof(AttendanceRecord))
        {
            RecordId = record.Id, ZoneId = zoneId, Reason = before is null ? null : entry.Reason, Before = before, After = Snapshot(record),
        });
    }

    private static bool Changed(AttendanceRecord r, AttendanceEntry e) =>
        r.Result != e.Result || r.Hours != e.Hours || r.Notes != e.Notes || r.ContractorId != e.TeamOfDayId;

    private static object Snapshot(AttendanceRecord r) => new
    {
        r.WorkDate, r.EmployeeId, r.WorkerId, Result = r.Result.ToString(), r.Hours, r.Notes, TeamOfDayId = r.ContractorId,
    };
}
