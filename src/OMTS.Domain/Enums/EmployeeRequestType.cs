namespace OMTS.Domain.Enums;

/// <summary>What an employee asks the Cooperative Owner for (BR-LABOR-08).</summary>
public enum EmployeeRequestType
{
    /// <summary>One or more days of leave; only recorded, it changes no pay.</summary>
    Leave,

    /// <summary>The check-in of one day was missed: the requested time is recorded when approved.</summary>
    MissedCheckIn,

    /// <summary>The check-out of one day was missed.</summary>
    MissedCheckOut,
}
