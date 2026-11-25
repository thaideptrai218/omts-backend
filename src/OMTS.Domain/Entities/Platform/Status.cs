namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E51 – Status catalog value. Codes are fixed by the SRS (BR-29); display texts change only through
/// seed-data migrations. Ids are listed in <see cref="Constants.StatusIds"/>.
/// </summary>
public class Status
{
    public int Id { get; set; }
    public string Group { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public int SortOrder { get; set; }
}
