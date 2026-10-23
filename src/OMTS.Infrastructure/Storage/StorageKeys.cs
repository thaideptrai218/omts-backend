using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Builds storage keys <c>{category}/{yyyy}/{MM}/{guid}{ext}</c>. The extension comes from the detected
/// file format, so keys never carry client file names and a stored file is always served as what it is.
/// </summary>
internal static partial class StorageKeys
{
    public static string Create(string category, FileFormat format, DateTimeOffset now)
    {
        var safeCategory = SafeCategory().Replace(category.Trim().ToLowerInvariant(), "-").Trim('-');
        if (safeCategory.Length == 0)
        {
            throw new ArgumentException("A storage category is required.", nameof(category));
        }

        var month = now.ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
        return $"{safeCategory}/{month}/{Guid.NewGuid():N}{FileValidator.ExtensionOf(format)}";
    }

    /// <summary>True for keys this class could have produced (no "..", no absolute paths, known extension).</summary>
    public static bool IsValid(string key) => ValidKey().IsMatch(key);

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SafeCategory();

    [GeneratedRegex(@"\A[a-z0-9-]+/[0-9]{4}/[0-9]{2}/[a-f0-9]{32}\.(jpg|png|pdf|webp)\z")]
    private static partial Regex ValidKey();
}
