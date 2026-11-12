using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// The history of a farming record (decision of 12/11/2026): who did what and when, which fields went from which value to which, and the reason, from the audit snapshots. It is read
/// with the same permission as the record itself in the zone of its events (View of Harvests, Cultivation cycles or Cultivation logs); the Cooperative Owner reads every zone. A record that was
/// removed keeps its history. A record with no event at all answers 404. Reading changes nothing.
/// </summary>
public sealed partial class RecordHistoryService(IAuditEventQuery events, ZoneAccessGuard zoneAccess)
{
    public const string HarvestLot = "HarvestLot";
    public const string SeedingRecord = "SeedingRecord";
    public const string MaterialUsage = "MaterialUsage";
    public const string CultivationLog = "CultivationLog";
    public const string CultivationCycle = "CultivationCycle";
    public const string NurseryBatch = "NurseryBatch";

    /// <summary>Snapshot keys that are not shown as a change.</summary>
    private static readonly HashSet<string> Hidden = new(["RowVersion", "AffectedPublishedLotCodes"], StringComparer.Ordinal);

    public async Task<Result<RecordHistory>> ReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var function = recordType switch
        {
            HarvestLot => PermissionFunctions.Harvests,
            CultivationLog => PermissionFunctions.CultivationLogs,
            _ => PermissionFunctions.CultivationCycles,
        };
        var rows = await events.HistoryOfRecordAsync(recordType, recordId, cancellationToken);
        if (rows.Count == 0)
        {
            return AppError.NotFound;
        }

        var zoneId = rows.Select(r => r.Values.Event.ZoneId).FirstOrDefault(z => z is not null);
        var allowed = zoneId is { } zone
            ? await zoneAccess.AllowsAsync(function, PermissionAction.View, zone, cancellationToken)
            : await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        if (!allowed)
        {
            return AppError.Forbidden;
        }

        return new RecordHistory(recordType, recordId, rows.Select(r => r.Values.Event.RecordCode).LastOrDefault(c => c is not null), rows.Select(r => Entry(recordType, r)).ToList());
    }

    private static RecordHistoryEntry Entry(string recordType, AuditHistoryRow row)
    {
        var e = row.Values.Event;
        var action = RecordHistoryLabels.ActionOf(e.Action);
        var name = e.ActorEmployeeId is not null ? e.ActorEmployeeName : e.ActorAccountName;
        var before = AuditValueSerializer.ForDisplay(row.Values.Before);
        var after = AuditValueSerializer.ForDisplay(row.Values.After);
        return new RecordHistoryEntry(
            e.Id, e.OccurredAt, name ?? "Hệ thống", RecordHistoryLabels.RoleLabel(row.ActorRole), row.ActorRole, action, e.Action, RecordHistoryLabels.ActionLabel(action), e.Reason,
            NoteOf(after), Changes(recordType, before, after));
    }

    /// <summary>The published lots a correction changed are named: their public page shows the correction at once (BR-TRACE-05).</summary>
    private static string? NoteOf(JsonElement? after) =>
        after is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("AffectedPublishedLotCodes", out var codes) && codes.ValueKind == JsonValueKind.Array
            ? $"Lô đã công bố bị ảnh hưởng: {string.Join(", ", codes.EnumerateArray().Select(c => c.ToString()))}"
            : null;

    private static List<RecordHistoryChange> Changes(string recordType, JsonElement? before, JsonElement? after)
    {
        var b = before is { ValueKind: JsonValueKind.Object } bo ? bo.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        var a = after is { ValueKind: JsonValueKind.Object } ao ? ao.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) : [];
        var changes = new List<RecordHistoryChange>();
        foreach (var field in a.Keys.Concat(b.Keys).Distinct().Where(k => !Hidden.Contains(k)))
        {
            var from = b.TryGetValue(field, out var was) ? Text(was) : null;
            var to = a.TryGetValue(field, out var now) ? Text(now) : null;
            if (from == to)
            {
                continue;
            }

            var (label, unit) = RecordHistoryLabels.Field(recordType, field);
            changes.Add(new RecordHistoryChange(field, label, from, to, unit));
        }

        return changes;
    }

    /// <summary>A stored value as a reader writes it: dates as dd/MM/yyyy, times in Vietnam, lists joined, yes/no for a flag, null as nothing.</summary>
    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "Có",
        JsonValueKind.False => "Không",
        JsonValueKind.Number => value.GetDecimal().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.String => TextOfString(value.GetString() ?? string.Empty),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Text)),
        _ => value.GetRawText(),
    };

    private static string TextOfString(string text)
    {
        if (DateOnlyPattern().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        return DateTimePattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at.ToOffset(BusinessClock.UtcOffset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : text;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateOnlyPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T")]
    private static partial Regex DateTimePattern();
}
