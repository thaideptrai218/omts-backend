using OMTS.Application.Common.Interfaces.Addresses;

namespace OMTS.Application.Features.AdministrativeUnits;

/// <summary>The province an address ends with and the parts before it.</summary>
public sealed record ProvinceSplit(ProvinceUnit Province, IReadOnlyList<string> Rest);

/// <param name="Ward">Null when no ward of the province is named in the address.</param>
/// <param name="Street">What stands before the ward; null when nothing does.</param>
public sealed record AddressSplit(ProvinceUnit Province, WardUnit? Ward, string? Street);

/// <summary>
/// Matches the names of an address line to the codes of the administrative units list. An address reads "street, ward, province": the province
/// is its last part (a closing "Việt Nam" is ignored) and the ward the part before it, or the one before that when an old district stands between.
/// </summary>
public static class AddressSplitter
{
    private static readonly HashSet<string> Countries = ["viet nam", "vietnam", "vn"];

    public static ProvinceUnit? MatchProvince(string? text, IReadOnlyList<ProvinceUnit> provinces)
    {
        var key = AddressNames.Key(text, AddressLevel.Province);
        return key.Length == 0 ? null : provinces.FirstOrDefault(p => AddressNames.Key(p.Name, AddressLevel.Province) == key);
    }

    /// <summary>When two wards share a name (a phường and a xã), the one whose prefix the text has.</summary>
    public static WardUnit? MatchWard(string? text, IReadOnlyList<WardUnit> wards)
    {
        var key = AddressNames.Key(text, AddressLevel.Ward);
        if (key.Length == 0)
        {
            return null;
        }

        var same = wards.Where(w => AddressNames.Key(w.Name, AddressLevel.Ward) == key).ToList();
        return same.Count > 1 && AddressNames.WardKind(text) is { } kind
            ? same.FirstOrDefault(w => AddressNames.WardKind(w.Name) == kind) ?? same[0]
            : same.FirstOrDefault();
    }

    /// <summary>The province the address ends with, or null when its last part names none.</summary>
    public static ProvinceSplit? SplitProvince(string? address, IReadOnlyList<ProvinceUnit> provinces)
    {
        var parts = (address ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        while (parts.Count > 0 && Countries.Contains(AddressNames.Key(parts[^1], AddressLevel.Province)))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count == 0 || MatchProvince(parts[^1], provinces) is not { } province)
        {
            return null;
        }

        return new ProvinceSplit(province, parts.Take(parts.Count - 1).ToList());
    }

    /// <summary>The ward among the last two parts before the province, and the street before it.</summary>
    public static AddressSplit SplitWard(ProvinceSplit split, IReadOnlyList<WardUnit> wards)
    {
        var rest = split.Rest;
        for (var index = rest.Count - 1; index >= Math.Max(0, rest.Count - 2); index--)
        {
            if (MatchWard(rest[index], wards) is { } ward)
            {
                return new AddressSplit(split.Province, ward, Joined(rest.Take(index)));
            }
        }

        return new AddressSplit(split.Province, null, Joined(rest));
    }

    private static string? Joined(IEnumerable<string> parts) => parts.ToList() is { Count: > 0 } list ? string.Join(", ", list) : null;
}
