using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>A unit of measure of the cooperative's catalogue (con, kg, bao, thùng…): the list of the settings screen. The name is unique regardless of letter case.</summary>
public class InputUnit : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A unit an item is counted in. <see cref="Factor"/> is how many base units one of it holds; the base unit has factor 1. The code is unique across all
/// item units and also differs from every item code.
/// </summary>
public class InputItemUnit : Entity
{
    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;
    public Guid UnitId { get; set; }
    public InputUnit Unit { get; set; } = null!;
    public decimal Factor { get; set; } = 1m;
    public string Code { get; set; } = string.Empty;
    public bool IsBase { get; set; }
}

/// <summary>
/// A user group of items ("Nhóm hàng"), ordered by <see cref="SortOrder"/>. A group may sit under a parent group (two levels at most: a parent is never itself a child).
/// Deleted only when it has no child group and no item belongs to it.
/// </summary>
public class InputItemCategory : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Guid? ParentId { get; set; }
    public InputItemCategory? Parent { get; set; }
}

/// <summary>A photo of an item (at most five), kept in file storage under <see cref="StorageKey"/>.</summary>
public class InputItemPhoto : Entity
{
    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;
    public string StorageKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
