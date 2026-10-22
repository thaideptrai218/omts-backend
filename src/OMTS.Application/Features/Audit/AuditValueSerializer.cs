using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// Turns before/after snapshots into JSON for <see cref="Domain.Entities.Platform.AuditEvent"/> and drops
/// every field that may hold a password, temporary password, verification code or token (NFR-01),
/// at any depth.
/// </summary>
internal static partial class AuditValueSerializer
{
    private static readonly HashSet<string> SensitiveWords =
        new(["password", "passwd", "pwd", "otp", "token", "secret", "credential", "hash"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        // Entities can be passed as they are; back-references to loaded navigations are skipped.
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string? Serialize(object? values)
    {
        if (values is null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(values, values.GetType(), Options);
        RemoveSensitiveFields(node);
        return node?.ToJsonString();
    }

    private static void RemoveSensitiveFields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Where(p => IsSensitive(p.Key, p.Value)).Select(p => p.Key).ToList())
                {
                    obj.Remove(name);
                }

                foreach (var (_, child) in obj)
                {
                    RemoveSensitiveFields(child);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RemoveSensitiveFields(item);
                }

                break;
        }
    }

    /// <summary>
    /// Matches whole words of the field name, so "PasswordHash", "otp_code" and "RefreshToken" are removed
    /// but "Footprint" is not. Flags and times about a secret ("MustChangePassword",
    /// "TemporaryPasswordIssuedAt") are not secrets and stay in the log.
    /// </summary>
    private static bool IsSensitive(string name, JsonNode? value)
    {
        var words = WordBoundary().Split(name).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0 || IsFlagOrTime(words, value))
        {
            return false;
        }

        return words.Any(SensitiveWords.Contains)
            || string.Concat(words).Contains("verificationcode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlagOrTime(string[] words, JsonNode? value) =>
        value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
        || words[^1].Equals("at", StringComparison.OrdinalIgnoreCase);

    // Splits camelCase, PascalCase, acronyms ("OTPCode") and snake/kebab case into words.
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-\s.]+")]
    private static partial Regex WordBoundary();
}
