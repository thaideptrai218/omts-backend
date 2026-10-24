using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Status filter values of the ENV-03 lists and the derived alert state.</summary>
public static class WaterMeasurementStatuses
{
    /// <summary>Object name used in MSG61.</summary>
    public const string ObjectName = "water measurement";

    public static readonly IReadOnlyList<int> All = [S.Draft, S.Confirmed, S.Superseded, S.Voided];

    private static readonly Dictionary<string, int[]> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Draft"] = [S.Draft],
        ["Confirmed"] = [S.Confirmed],
        ["Corrected"] = [S.Superseded],
        ["Superseded"] = [S.Superseded],
        ["Voided"] = [S.Voided],
        ["All"] = [S.Draft, S.Confirmed, S.Superseded, S.Voided],
    };

    /// <summary>
    /// Status ids of a filter given as repeated and/or comma-separated names (Draft, Confirmed, Corrected,
    /// Voided, All). Empty means Confirmed only; an unknown name gives null.
    /// </summary>
    public static IReadOnlyList<int>? ParseFilter(IEnumerable<string>? values)
    {
        var names = (values ?? [])
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (names.Count == 0)
        {
            return [S.Confirmed];
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

    /// <summary>409 MSG61 for a transition the record's status does not allow.</summary>
    public static AppError CannotChange(IStatusCatalog statusCatalog, int currentStatusId, int newStatusId, string reason) =>
        AppError.Conflict(UserMessages.CannotChangeStatus(
            ObjectName, statusCatalog.DisplayNameOf(currentStatusId), statusCatalog.DisplayNameOf(newStatusId), reason));

    public static WaterAlertState AlertStateOf(WaterMeasurement measurement) => !measurement.ThresholdExceeded
        ? WaterAlertState.None
        : measurement.StatusId switch
        {
            S.Confirmed => WaterAlertState.Active,
            S.Superseded => WaterAlertState.Corrected,
            S.Voided => WaterAlertState.Voided,
            _ => WaterAlertState.None,
        };
}
