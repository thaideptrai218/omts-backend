namespace OMTS.Domain.Constants;

/// <summary>Fixed primary keys of the seeded issue types, so code can point at them and every environment holds the same rows.</summary>
public static class InputIssueTypeIds
{
    public static readonly Guid SeedRelease = new("6e1f0b20-0000-4d4a-8b20-000000000001");
    public static readonly Guid RaftRepair = new("6e1f0b20-0000-4d4a-8b20-000000000002");
    public static readonly Guid ZoneShared = new("6e1f0b20-0000-4d4a-8b20-000000000003");
    public static readonly Guid Nursery = new("6e1f0b20-0000-4d4a-8b20-000000000004");
}
