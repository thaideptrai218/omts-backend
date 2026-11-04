using System.Globalization;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The lot code (BR-HARVEST-03): the raft code and the harvest date as yyyyMMdd, with the zone code put first when the raft code does not start with it, as KV01-B01-20261026.
/// The second and later lots of the raft that day add -2, -3 and so on.
/// </summary>
public static class HarvestLotCodes
{
    private const char Separator = '-';

    /// <summary>
    /// What every lot code of the raft starts with. "Starts with the zone code" takes the separator into account (PM default): raft KV10-B01 of zone KV1 does not start with it.
    /// </summary>
    public static string RaftPart(string zoneCode, string raftCode) =>
        raftCode.Equals(zoneCode, StringComparison.OrdinalIgnoreCase) || raftCode.StartsWith(zoneCode + Separator, StringComparison.OrdinalIgnoreCase)
            ? raftCode
            : zoneCode + Separator + raftCode;

    /// <summary>The code of the first lot of the raft on the date.</summary>
    public static string Base(string zoneCode, string raftCode, DateOnly harvestDate) =>
        $"{RaftPart(zoneCode, raftCode)}{Separator}{harvestDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The next code after the highest one in use, so a code freed in the middle is not taken again. <paramref name="existing"/> holds the codes from <paramref name="baseCode"/>;
    /// another text that happens to start the same way does not count.
    /// </summary>
    public static string Next(string baseCode, IEnumerable<string> existing)
    {
        var highest = existing.Select(code => NumberOf(baseCode, code)).DefaultIfEmpty(0).Max();
        return highest == 0 ? baseCode : $"{baseCode}{Separator}{(highest + 1).ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>1 for the base code, n for base-n, 0 for anything else.</summary>
    private static int NumberOf(string baseCode, string code)
    {
        if (code.Equals(baseCode, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var suffixed = code.Length > baseCode.Length + 1 && code.StartsWith(baseCode + Separator, StringComparison.OrdinalIgnoreCase);
        return suffixed && int.TryParse(code.AsSpan(baseCode.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
