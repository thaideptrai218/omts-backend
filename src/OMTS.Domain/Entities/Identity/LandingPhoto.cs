using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>A picture the Cooperative Owner shows on the public introduction page of the cooperative.</summary>
public class LandingPhoto : Entity
{
    public LandingSection Section { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public int SortOrder { get; set; }
}
