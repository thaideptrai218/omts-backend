using System.Text.Json;
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
public sealed class RecordHistoryService(IAuditEventQuery events, ZoneAccessGuard zoneAccess)
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

        var parsed = rows.Select(r => (Row: r, Before: AuditValueSerializer.ForDisplay(r.Values.Before), After: AuditValueSerializer.ForDisplay(r.Values.After))).ToList();
        var references = await events.ReferenceNamesAsync(
            parsed.SelectMany(p => new[] { p.Before, p.After }).Where(v => v is not null).SelectMany(v => RecordHistoryValues.ReferencesIn(v!.Value)).Distinct().ToList(), cancellationToken);
        return new RecordHistory(
            recordType, recordId, rows.Select(r => r.Values.Event.RecordCode).LastOrDefault(c => c is not null),
            parsed.Select(p => Entry(recordType, p.Row, p.Before, p.After, references)).ToList());
    }

    private static RecordHistoryEntry Entry(string recordType, AuditHistoryRow row, JsonElement? before, JsonElement? after, IReadOnlyDictionary<Guid, string> references)
    {
        var e = row.Values.Event;
        var action = RecordHistoryLabels.ActionOf(e.Action);
        var name = e.ActorEmployeeId is not null ? e.ActorEmployeeName : e.ActorAccountName;
        return new RecordHistoryEntry(
            e.Id, e.OccurredAt, name ?? "Hệ thống", RecordHistoryLabels.RoleLabel(row.ActorRole), row.ActorRole, action, e.Action, RecordHistoryLabels.ActionLabel(action, recordType),
            RecordHistoryLabels.ReasonLabel(e.Reason), NoteOf(after), Changes(recordType, before, after, references));
    }

    /// <summary>The published lots a correction changed are named: their public page shows the correction at once (BR-TRACE-05).</summary>
    private static string? NoteOf(JsonElement? after) =>
        after is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("AffectedPublishedLotCodes", out var codes) && codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0
            ? $"Lô đã công khai bị ảnh hưởng: {string.Join(", ", codes.EnumerateArray().Select(c => c.ToString()))}"
            : null;

    /// <summary>
    /// The fields that differ between the two snapshots. An event with no values before (a creation) lists the values it set and an event with none after (a removal) the values it
    /// held: a field that was empty is left out, so nothing reads "— → —".
    /// </summary>
    internal static List<RecordHistoryChange> Changes(string recordType, JsonElement? before, JsonElement? after, IReadOnlyDictionary<Guid, string> references)
    {
        var b = Fields(recordType, before, references);
        var a = Fields(recordType, after, references);
        var hasBefore = b.Count > 0;
        var hasAfter = a.Count > 0;
        var changes = new List<RecordHistoryChange>();
        foreach (var field in a.Keys.Concat(b.Keys).Distinct())
        {
            var from = b.GetValueOrDefault(field)?.Text;
            var to = a.GetValueOrDefault(field)?.Text;
            if (from == to || (to is null && !hasBefore) || (from is null && !hasAfter))
            {
                continue;
            }

            var named = a.GetValueOrDefault(field) ?? b[field];
            changes.Add(new RecordHistoryChange(field, named.Label, from, to, named.Unit));
        }

        return changes;
    }

    private sealed record Shown(string Label, string? Unit, string? Text);

    /// <summary>The fields of a snapshot as they are shown, by field name; the figures of a close are fields of their own and a field that is not shown is not there.</summary>
    private static Dictionary<string, Shown> Fields(string recordType, JsonElement? snapshot, IReadOnlyDictionary<Guid, string> references)
    {
        var fields = new Dictionary<string, Shown>(StringComparer.Ordinal);
        if (snapshot is not { ValueKind: JsonValueKind.Object } values)
        {
            return fields;
        }

        foreach (var property in values.EnumerateObject().Where(p => !Hidden.Contains(p.Name)))
        {
            if (recordType == CultivationCycle && property.Name == "Summary" && property.Value.ValueKind == JsonValueKind.Object)
            {
                var unitOfSeed = property.Value.TryGetProperty("SeedUnit", out var seedUnit) && seedUnit.ValueKind == JsonValueKind.String ? seedUnit.GetString() : null;
                foreach (var figure in property.Value.EnumerateObject())
                {
                    if (RecordHistoryLabels.SummaryField(figure.Name) is { } known)
                    {
                        fields[figure.Name] = new Shown(known.Label, figure.Name == "SeedHung" ? unitOfSeed : known.Unit, RecordHistoryValues.Format(recordType, figure.Name, figure.Value, references));
                    }
                }

                continue;
            }

            var (label, unit) = RecordHistoryLabels.Field(recordType, property.Name);
            fields[property.Name] = new Shown(label, unit, RecordHistoryValues.Format(recordType, property.Name, property.Value, references));
        }

        return fields;
    }
}
