namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E51 – Status catalog value. Codes are fixed by the SRS (BR-29); the System Administrator only edits
/// display name, meaning, order and color (UC73). Ids are listed in <see cref="Constants.StatusIds"/>.
/// </summary>
public class Status
{
    public int Id { get; set; }
    public string Group { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Meaning { get; set; }
    public bool IsInitial { get; set; }
    public bool IsFinal { get; set; }
    public int SortOrder { get; set; }
    public string? DisplayColor { get; set; }
}
