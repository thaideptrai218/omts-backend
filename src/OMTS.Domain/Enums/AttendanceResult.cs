namespace OMTS.Domain.Enums;

/// <summary>Attendance result per person per day (LAB-04). Present and PaidLeave are paid days; hours never change pay.</summary>
public enum AttendanceResult
{
    Present,
    PaidLeave,
    UnpaidLeave,
}
