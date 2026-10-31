using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// The anonymous file URLs of the cooperative block (logo, cover photo): <c>{publicPath}/{yyyy}/{MM}/{32 hex}.{ext}</c>,
/// the storage key below its category. A URL can only ever reach a file of its own category.
/// </summary>
public static partial class CooperativePublicFiles
{
    /// <summary>Every logo file, current or replaced (old logos are kept).</summary>
    public const string LogoFilesPath = "/api/public/cooperative/logos";

    /// <summary>The current logo, whatever it is when the page is opened.</summary>
    public const string CurrentLogoPath = "/api/public/cooperative/logo";

    /// <summary>The current cover photo only: a replaced or removed one is deleted.</summary>
    public const string CoverPhotoFilesPath = "/api/public/cooperative/cover-photos";

    /// <summary>The public URL of a stored key of <paramref name="category"/>, or null.</summary>
    public static string? UrlOf(string? key, string category, string publicPath) =>
        key is not null && key.StartsWith($"{category}/", StringComparison.Ordinal) ? $"{publicPath}/{key[(category.Length + 1)..]}" : null;

    /// <summary>The storage key a public path ("2026/10/{id}.png") names in the category, or null when it has another shape.</summary>
    public static string? KeyOf(string path, string category) => FilePattern().IsMatch(path) ? $"{category}/{path}" : null;

    /// <summary>The stored content with its content type, or null when there is no such file.</summary>
    public static async Task<(Stream Content, string ContentType)?> OpenAsync(IFileStorage storage, string? key, CancellationToken cancellationToken)
    {
        if (key is null || FileValidator.ContentTypeOfKey(key) is not { } contentType || await storage.OpenReadAsync(key, cancellationToken) is not { } content)
        {
            return null;
        }

        return (content, contentType);
    }

    [GeneratedRegex(@"\A[0-9]{4}/[0-9]{2}/[a-f0-9]{32}\.(jpg|png|webp)\z")]
    private static partial Regex FilePattern();
}
