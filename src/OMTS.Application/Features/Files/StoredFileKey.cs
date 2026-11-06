using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Files;

/// <summary>
/// The one written form of a storage key: <c>{category}/{yyyy}/{MM}/{guid}{ext}</c>, and for a meter photo
/// <c>water-meter-photos/{account}/{yyyy}/{MM}/{guid}{ext}</c>. Records compare keys as text while blob storage normalizes
/// "." segments and backslashes, so another spelling would open the same file without being the same key.
/// </summary>
public static partial class StoredFileKey
{
    private const string OwnerFolder = FileCategories.WaterMeterPhotos + "/[a-f0-9]{32}";

    /// <summary>True for keys the storage could have produced (no "..", no absolute paths, known extension).</summary>
    public static bool IsValid(string key) => KeyPattern().IsMatch(key);

    /// <summary>True for <c>water-meter-photos/{account}</c>, the only category written with an owner folder.</summary>
    public static bool IsOwnerFolder(string category) => OwnerFolderPattern().IsMatch(category);

    [GeneratedRegex(@"\A(?:" + OwnerFolder + @"|[a-z0-9-]+)/[0-9]{4}/[0-9]{2}/[a-f0-9]{32}\.(jpg|png|pdf|webp)\z")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\A" + OwnerFolder + @"\z")]
    private static partial Regex OwnerFolderPattern();
}
