namespace OMTS.Domain.Enums;

/// <summary>
/// Where a person chosen for a harvest plan stands (owner decision of 26/11/2026). The people counted as present for the plan are Confirmed, ConfirmedManually and External;
/// Invited and Declined are not.
/// </summary>
public enum HarvestPlanStaffState
{
    /// <summary>Chosen by the Owner (or the Zone Manager of the plan), waiting for the answer of the employee in the mobile app.</summary>
    Invited = 0,

    /// <summary>The employee accepted in the mobile app.</summary>
    Confirmed = 1,

    /// <summary>The Owner (or the Zone Manager of the plan) confirmed on behalf of the employee, with a reason.</summary>
    ConfirmedManually = 2,

    /// <summary>The employee declined; the row stays so the Owner sees it and can replace the person.</summary>
    Declined = 3,

    /// <summary>Hired on the spot outside the system ("Bổ sung nhân công"): no confirmation needed, counts as confirmed at once.</summary>
    External = 4,
}
