using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>One notification category/channel switch of an account (owned by <see cref="Account"/>).</summary>
public class NotificationPreference
{
    public string Category { get; set; } = string.Empty;
    public NotificationChannel Channel { get; set; }
    public bool Enabled { get; set; } = true;
}
