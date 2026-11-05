using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// There are no attendance or work assignment rows yet: the old tables are gone and their replacements come later. LABOR-04
/// (L5) reads its Attendance table in <see cref="LastAttendanceDayAsync"/>, and LABOR-02 (L4) removes its WorkAssignment rows in
/// <see cref="RemoveAssignmentsAfterAsync"/>.
/// </summary>
internal sealed class EmployeeWorkRecords : IEmployeeWorkRecords
{
    public Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) =>
        Task.FromResult<DateOnly?>(null);

    public Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
