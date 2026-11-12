namespace OMTS.Domain.Enums;

/// <summary>Use takes input items out for work in a zone (seeding, raft repair, shared use); Disposal writes them off (spoiled, lost, or the item was deleted).</summary>
public enum InputIssueKind
{
    Use,
    Disposal,
}
