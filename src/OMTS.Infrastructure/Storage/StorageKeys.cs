using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Builds storage keys <c>{category}/{yyyy}/{MM}/{guid}{ext}</c>, with an owner folder when the category is given as
/// <c>{category}/{owner}</c>. The extension comes from the detected file format, so keys never carry client file names
/// and a stored file is always served as what it is.
/// </summary>
internal static partial class StorageKeys
{
    public static string Create(string category, FileFormat format, DateTimeOffset now)
    {
        var folders = category.Trim().ToLowerInvariant().Split('/', 2)
            .Select(folder => SafeCategory().Replace(folder, "-").Trim('-'))
            .ToList();
        if (folders.Any(folder => folder.Length == 0))
        {
            throw new ArgumentException("A storage category is required.", nameof(category));
        }

        var month = now.ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
        return $"{string.Join('/', folders)}/{month}/{Guid.NewGuid():N}{FileValidator.ExtensionOf(format)}";
    }

    /// <summary>True for keys this class could have produced (no "..", no absolute paths, known extension).</summary>
    public static bool IsValid(string key) => ValidKey().IsMatch(key);

    /// <summary>
    /// Key prefix <c>{category}/</c> of a category's files (owner folders included), to list them. The category must
    /// already be written as <see cref="Create"/> writes it, since it becomes a folder or a blob prefix.
    /// </summary>
    public static string ListPrefix(string category) =>
        CategoryName().IsMatch(category) ? $"{category}/" : throw new ArgumentException("Invalid storage category.", nameof(category));

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SafeCategory();

    [GeneratedRegex(@"\A[a-z0-9-]+\z")]
    private static partial Regex CategoryName();

    [GeneratedRegex(@"\A[a-z0-9-]+(?:/[a-z0-9-]+)?/[0-9]{4}/[0-9]{2}/[a-f0-9]{32}\.(jpg|png|pdf|webp)\z")]
    private static partial Regex ValidKey();
}
