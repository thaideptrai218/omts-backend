using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E46 – In-app notification. Unread while <see cref="ReadAt"/> is empty. The related record is a
/// type + code, not a foreign key; opening it re-checks permissions.
/// </summary>
public class Notification : Entity
{
    public Guid RecipientAccountId { get; set; }
    public Account RecipientAccount { get; set; } = null!;

    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }

    public string? RelatedRecordType { get; set; }
    public string? RelatedRecordCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Water alerts, new orders and sent QRs cannot be turned off.</summary>
    public bool IsMandatory { get; set; }
}
