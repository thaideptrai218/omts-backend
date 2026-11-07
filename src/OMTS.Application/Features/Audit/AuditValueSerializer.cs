using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// Turns before/after snapshots into JSON for <see cref="Domain.Entities.Platform.AuditEvent"/> and drops every field
/// that may hold a password, temporary password, verification code, login code of the employee app or token (GBR-02,
/// NFR-01), at any depth and inside a JSON document kept as text: when the row is written, and again when it is shown.
/// </summary>
internal static partial class AuditValueSerializer
{
    private static readonly HashSet<string> SensitiveWords =
        new(["password", "passwd", "pwd", "otp", "token", "secret", "credential", "hash"], StringComparer.OrdinalIgnoreCase);

    // The whole name in lower case, without separators: "newpassword", "logincode", "refreshtoken", "clientsecret".
    // Short words that are also inside harmless names ("Footprint", "Tokenomics", "Secretary") only count at the end.
    private static readonly string[] SensitiveParts = ["password", "passwd", "credential", "verificationcode", "logincode"];
    private static readonly string[] SensitiveEndings = ["token", "secret", "hash", "otp", "pwd"];

    private static readonly JsonSerializerOptions Options = new()
    {
        // Entities can be passed as they are; back-references to loaded navigations are skipped.
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string? Serialize(object? values) =>
        values is null ? null : Clean(JsonSerializer.SerializeToNode(values, values.GetType(), Options))?.ToJsonString();

    /// <summary>
    /// A stored before/after value as shown by the audit search (ACCOUNT-10), with the same fields removed again, so a
    /// row written by an older version or by hand never shows a secret. A value that is not JSON, or repeats a field
    /// name, is not shown (null).
    /// </summary>
    public static JsonElement? ForDisplay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return Clean(JsonNode.Parse(json)) is { } node ? JsonSerializer.SerializeToElement(node) : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // ArgumentException: a repeated name, found when the object is first read.
            return null;
        }
    }

    /// <summary>
    /// The node without its secrets: objects and arrays are cleaned in place; a text holding a JSON object or array
    /// becomes the same text cleaned.
    /// </summary>
    private static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Where(p => IsSensitive(p.Key, p.Value)).Select(p => p.Key).ToList())
                {
                    obj.Remove(name);
                }

                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[name];
                    if (Clean(child) is var cleaned && !ReferenceEquals(cleaned, child))
                    {
                        obj[name] = cleaned;
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    if (Clean(item) is var cleaned && !ReferenceEquals(cleaned, item))
                    {
                        array[i] = cleaned;
                    }
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return CleanText(value, text);
            default:
                return node;
        }
    }

    /// <summary>A text that is a JSON object or array, cleaned; other text as it is; a document with a repeated name, null.</summary>
    private static JsonNode? CleanText(JsonValue value, string text)
    {
        var start = text.AsSpan().TrimStart();
        if (start.IsEmpty || start[0] is not ('{' or '['))
        {
            return value;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Plain text that only starts like JSON.
            return value;
        }

        if (document is not (JsonObject or JsonArray))
        {
            return value;
        }

        try
        {
            // The text is kept as written unless a secret was removed from it.
            var written = document.ToJsonString();
            var cleaned = Clean(document)!.ToJsonString();
            return cleaned == written ? value : JsonValue.Create(cleaned);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Matches whole words of the field name, so "PasswordHash", "otp_code" and "RefreshToken" are removed but
    /// "Footprint" is not; a final "s" is dropped first ("Passwords", "RefreshTokens"), and the whole name in lower case
    /// is matched too ("newpassword", "logincode"). Flags and times about a secret ("MustChangePassword",
    /// "TemporaryPasswordIssuedAt") are not secrets and stay in the log.
    /// </summary>
    private static bool IsSensitive(string name, JsonNode? value)
    {
        var words = WordBoundary().Split(name).Where(w => w.Length > 0).Select(Singular).ToArray();
        if (words.Length == 0 || IsFlagOrTime(words, value))
        {
            return false;
        }

        var whole = string.Concat(words).ToLowerInvariant();
        return words.Any(SensitiveWords.Contains)
            || SensitiveParts.Any(part => whole.Contains(part, StringComparison.Ordinal))
            || SensitiveEndings.Any(ending => whole.EndsWith(ending, StringComparison.Ordinal));
    }

    private static string Singular(string word) => word.Length > 1 && word[^1] is 's' or 'S' ? word[..^1] : word;

    private static bool IsFlagOrTime(string[] words, JsonNode? value) =>
        value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
        || words[^1].Equals("at", StringComparison.OrdinalIgnoreCase);

    // Splits camelCase, PascalCase, acronyms ("OTPCode") and snake/kebab case into words.
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-\s.]+")]
    private static partial Regex WordBoundary();
}
