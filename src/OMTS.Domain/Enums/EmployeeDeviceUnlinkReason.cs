namespace OMTS.Domain.Enums;

/// <summary>Why the linked device of an employee stopped being linked (BR-LABOR-02).</summary>
public enum EmployeeDeviceUnlinkReason
{
    /// <summary>A login code was entered on another device, which became the linked one (LABOR-06 step 7).</summary>
    NewDevice,

    /// <summary>The phone number of the profile changed or was cleared (LABOR-01.1).</summary>
    PhoneChanged,

    /// <summary>The day after the last working day came, or the employment was ended with a day before today (LABOR-01.3).</summary>
    EmploymentEnded,
}
