using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>
/// E05 – Permissions of a role on one function. Composite key (RoleId, FunctionCode); the function
/// catalog is fixed by the system. View is mandatory when any other permission is granted.
/// </summary>
public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public string FunctionCode { get; set; } = string.Empty;

    public bool CanCreate { get; set; }
    public bool CanView { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }

    /// <summary>Other permissions supported by the function, e.g. "EXPORT", "IMPORT".</summary>
    public List<string> OtherPermissions { get; set; } = [];

    public DataScope DataScope { get; set; } = DataScope.AssignedZone;
}
