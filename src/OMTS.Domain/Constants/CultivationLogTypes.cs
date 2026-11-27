namespace OMTS.Domain.Constants;

/// <summary>The kind of visit a cultivation (care) log records; stored and sent as these codes. Vietnamese labels belong to the clients.</summary>
public static class CultivationLogTypes
{
    /// <summary>Cho ăn.</summary>
    public const string Feeding = "FEEDING";

    /// <summary>Vệ sinh bè/lồng.</summary>
    public const string Cleaning = "CLEANING";

    /// <summary>Kiểm tra.</summary>
    public const string Inspection = "INSPECTION";

    /// <summary>Phòng/trị bệnh.</summary>
    public const string Disease = "DISEASE";

    /// <summary>Sửa chữa.</summary>
    public const string Repair = "REPAIR";

    /// <summary>Khác: the type of a log that names none, and of every log saved before types existed.</summary>
    public const string Other = "OTHER";

    /// <summary>The longest code, for the column.</summary>
    public const int MaxLength = 20;

    public static IReadOnlyList<string> All { get; } = [Feeding, Cleaning, Inspection, Disease, Repair, Other];

    /// <summary>
    /// The code behind <paramref name="value"/> (trimmed, any letter case), <see cref="Other"/> when it is null or blank; null when it names no type.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Other;
        }

        return All.FirstOrDefault(code => string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
