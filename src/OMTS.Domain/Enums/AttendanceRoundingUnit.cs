namespace OMTS.Domain.Enums;

/// <summary>The unit the hours of a job type in a day are rounded down to (BR-LABOR-05); each value is its number of minutes.</summary>
public enum AttendanceRoundingUnit
{
    None = 0,
    Minutes15 = 15,
    Minutes30 = 30,
    Hour1 = 60,
}
