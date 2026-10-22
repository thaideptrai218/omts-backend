namespace OMTS.Domain.Enums;

/// <summary>E49 record type: exactly one Raft Creation per raft, then Replacements (BR-27).</summary>
public enum RaftMaterialRecordType
{
    RaftCreation,
    Replacement,
}
