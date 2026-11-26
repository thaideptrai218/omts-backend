using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// Owner decision of 26/11/2026 ("cách A"): a soft-deleted account frees its login identifiers so a NEW account may take the same username, phone number and email.
/// The deleted row keeps its id and display name and gets a marker that keeps the unique indexes satisfied and the history readable:
/// username <c>name~deleted-1a2b3c4d</c>, email <c>mail@x.vn~deleted-1a2b3c4d</c>, phone <c>0912345678~1a2b3c4d</c> (the phone column is only 20 long); the 8 hex digits
/// are the start of the account id. Idempotent. Base values are cut when the column would overflow.
/// </summary>
public static class DeletedAccountIdentifiers
{
    public const string Marker = "~deleted-";
    private const int UsernameMax = 50;
    private const int PhoneMax = 20;
    private const int EmailMax = 256;
    private const int ShortIdLength = 8;

    public static bool IsReleased(Account account) => account.Username.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Renames the username, phone number and email of a deleted account (no-op when already released).</summary>
    public static void Release(Account account)
    {
        if (IsReleased(account))
        {
            return;
        }

        var id = account.Id.ToString("N")[..ShortIdLength];
        account.Username = Mark(account.Username, Marker + id, UsernameMax);
        if (!string.IsNullOrEmpty(account.PhoneNumber))
        {
            account.PhoneNumber = Mark(account.PhoneNumber, "~" + id, PhoneMax);
        }

        if (!string.IsNullOrEmpty(account.Email))
        {
            account.Email = Mark(account.Email, Marker + id, EmailMax);
        }
    }

    private static string Mark(string value, string suffix, int max) =>
        (value.Length + suffix.Length <= max ? value : value[..(max - suffix.Length)]) + suffix;
}
