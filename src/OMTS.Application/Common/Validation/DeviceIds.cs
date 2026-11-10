namespace OMTS.Application.Common.Validation;

/// <summary>
/// The random id a browser or app install keeps and sends when it logs in. It only groups sessions into devices and is never
/// an authentication factor, so a client that cannot keep one sends none.
/// </summary>
public static class DeviceIds
{
    public const int MaxLength = 128;

    /// <summary>Base64url alphabet: what a random id encodes to, and nothing that needs escaping.</summary>
    public const string AllowedPattern = "^[A-Za-z0-9_-]*$";

    /// <summary>The value to hash, or null when the client sent no device: missing, empty, <c>unknown</c>, or not a valid id.</summary>
    public static string? Normalize(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || deviceId.Length > MaxLength
            || string.Equals(deviceId, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var c in deviceId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return null;
            }
        }

        return deviceId;
    }
}
