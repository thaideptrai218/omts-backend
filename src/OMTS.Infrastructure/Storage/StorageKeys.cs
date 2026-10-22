using System.Text.RegularExpressions;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Builds storage keys <c>{category}/{yyyy}/{MM}/{guid}{ext}</c>. The client file name only gives the
/// extension, so keys never carry personal names and cannot escape the category folder.
/// </summary>
internal static partial class StorageKeys
{
    public static string Create(string category, string fileName, DateTimeOffset now)
    {
        var safeCategory = SafeCategory().Replace(category.Trim().ToLowerInvariant(), "-").Trim('-');
        if (safeCategory.Length == 0)
        {
            throw new ArgumentException("A storage category is required.", nameof(category));
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!SafeExtension().IsMatch(extension))
        {
            extension = string.Empty;
        }

        return $"{safeCategory}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{extension}";
    }

    /// <summary>True for keys this class could have produced (no "..", no absolute paths).</summary>
    public static bool IsValid(string key) => ValidKey().IsMatch(key);

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SafeCategory();

    [GeneratedRegex(@"^\.[a-z0-9]{1,10}$")]
    private static partial Regex SafeExtension();

    [GeneratedRegex(@"^[a-z0-9-]+/\d{4}/\d{2}/[a-f0-9]{32}(\.[a-z0-9]{1,10})?$")]
    private static partial Regex ValidKey();
}
