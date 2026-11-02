using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>Attendance or work assignments of an employee dated after a day (they block ending the employment).</summary>
public sealed record LaborRecordsAfter(int AttendanceCount, int AssignmentCount)
{
    public int Total => AttendanceCount + AssignmentCount;
}
