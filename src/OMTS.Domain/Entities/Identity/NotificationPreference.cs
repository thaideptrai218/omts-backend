namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Whether one notification type is also pushed to the account's devices (ACCOUNT-07, BR-ACCOUNT-11); owned by
/// <see cref="Account"/>. A type without an entry is pushed. Every notification is kept in the list whatever this says
/// (BR-ACCOUNT-12).
/// </summary>
public class NotificationPreference
{
    public string Type { get; set; } = string.Empty;
    public bool Push { get; set; } = true;
}
