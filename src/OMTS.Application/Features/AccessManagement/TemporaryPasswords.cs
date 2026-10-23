using System.Security.Cryptography;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// Temporary passwords (ACS-09): 10 random characters with letters and digits, so they meet the password
/// rule; look-alike characters (0/O, 1/l/I) are left out because the Owner reads them out to the person.
/// </summary>
public static class TemporaryPasswords
{
    public const int Length = 10;

    private const string Letters = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string All = Letters + Digits;

    public static string New()
    {
        var chars = new char[Length];
        chars[0] = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        chars[1] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        for (var i = 2; i < Length; i++)
        {
            chars[i] = All[RandomNumberGenerator.GetInt32(All.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
