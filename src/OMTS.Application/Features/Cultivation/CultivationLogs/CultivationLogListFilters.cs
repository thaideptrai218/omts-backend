using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>Reads the query of the cross-raft log list: who may see which zones, then the filters and the page.</summary>
internal static class CultivationLogListFilters
{
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    /// <summary>
    /// A null answer means the list can be read. MSG-GEN-08 when the caller reads no zone or not the zone asked for (so an empty list never hides a refusal); then MSG-GEN-01
    /// for a <c>logType</c> that is no code, a period that ends before it starts, or a search that is too long.
    /// </summary>
    public static AppError? Read(
        CultivationLogListQuery query, PermittedZones allowed, out CultivationLogFilter filter, out int page, out int pageSize)
    {
        (filter, page, pageSize) = (new CultivationLogFilter(null, null, null, null, null, null, null), 1, 1);
        if (allowed.IsEmpty || (query.ZoneId is { } zone && !allowed.Allows(zone)))
        {
            return AppError.Forbidden;
        }

        if (CultivationLogValues.ReadTypeFilter(query.LogType, out var logType) is { } invalidType)
        {
            return invalidType;
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            return AppError.Invalid("To", "The end of the period is before its start.");
        }

        var search = query.Search?.Trim();
        if (search is { Length: > MaxSearchLength })
        {
            return AppError.Invalid("Search");
        }

        (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        filter = new CultivationLogFilter(
            query.ZoneId, query.RaftId, allowed.AllZones ? null : allowed.ZoneIds.ToHashSet(), logType, query.From, query.To, string.IsNullOrEmpty(search) ? null : search);
        return null;
    }
}
