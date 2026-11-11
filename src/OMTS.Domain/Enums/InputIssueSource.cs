namespace OMTS.Domain.Enums;

/// <summary>What produced a use issue: nothing (a manual issue), or a farming record that owns it and changes it (PM decision 11/11/2026, stock is the single source of quantity on hand).</summary>
public enum InputIssueSource
{
    None = 0,
    Seeding = 1,
    MaterialUsage = 2,
}
