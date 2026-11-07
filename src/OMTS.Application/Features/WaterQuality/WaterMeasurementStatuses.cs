using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Status filter values of the WATER-03 lists and the derived alert state.</summary>
public static class WaterMeasurementStatuses
{
    /// <summary>Object name used in MSG-GEN-09.</summary>
    public const string ObjectName = "water measurement";

    private static readonly Dictionary<string, int[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Active"] = [S.Active],
        ["Voided"] = [S.Voided],
        ["All"] = [S.Active, S.Voided],
    };

    /// <summary>
    /// Status ids of a filter given as repeated and/or comma-separated names (Active, Voided, All). Empty means both
    /// (WATER-03 step 3: a voided measurement stays in the history); an unknown name gives null.
    /// </summary>
    public static IReadOnlyList<int>? ParseFilter(IEnumerable<string?>? values)
    {
        var names = NamesIn(values);
        if (names.Count == 0)
        {
            return ByName["All"];
        }

        var ids = new HashSet<int>();
        foreach (var name in names)
        {
            if (!ByName.TryGetValue(name, out var matched))
            {
                return null;
            }

            ids.UnionWith(matched);
        }

        return ids.Order().ToList();
    }

    /// <summary>Whether the filter names a status: <c>status=</c> and <c>status=,</c> name none, so MSG-GEN-10 does not mention one.</summary>
    public static bool NamesStatus(IEnumerable<string?>? values) => NamesIn(values).Count > 0;

    // The model binder gives "status=" as one null element.
    private static List<string> NamesIn(IEnumerable<string?>? values) => (values ?? [])
        .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToList();

    /// <summary>409 MSG-GEN-09 for a change the record's status does not allow.</summary>
    public static AppError CannotChange(IStatusCatalog statusCatalog, int currentStatusId) =>
        AppError.Conflict(UserMessages.WrongStatus(ObjectName, statusCatalog.DisplayNameOf(currentStatusId)));

    public static WaterAlertState AlertStateOf(WaterMeasurement measurement) => !measurement.ThresholdExceeded
        ? WaterAlertState.None
        : measurement.StatusId == S.Voided ? WaterAlertState.Voided : WaterAlertState.Active;
}
