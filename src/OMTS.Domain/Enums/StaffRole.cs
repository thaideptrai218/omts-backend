namespace OMTS.Domain.Enums;

/// <summary>
/// The fixed roles the Cooperative Owner grants to staff accounts in zone–role pairs (BR-FARM-07). The third fixed
/// role, Cooperative Owner, is never granted: it is the subject type of the Owner's account.
/// </summary>
public enum StaffRole
{
    ZoneManager,
    Accountant,
}
