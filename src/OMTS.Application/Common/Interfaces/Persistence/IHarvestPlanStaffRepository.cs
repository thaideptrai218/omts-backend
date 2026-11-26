using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The people chosen to harvest a plan (the roster rows). Reads of one plan go through <see cref="IHarvestPlanRepository"/>, which loads the roster with the plan.</summary>
public interface IHarvestPlanStaffRepository
{
    /// <summary>The row with its plan (lines with raft and zone, the whole roster) and employee, tracked; null when there is none.</summary>
    Task<HarvestPlanStaffMember?> GetForEditAsync(Guid rowId, CancellationToken cancellationToken);

    /// <summary>
    /// The invitations of an employee, untracked, each with its plan (lines with raft and zone, the whole roster). Without a period: the plans still open (not Completed or Cancelled);
    /// with one: every plan planned in it (<paramref name="from"/> and <paramref name="to"/> included), whatever its status.
    /// </summary>
    Task<IReadOnlyList<HarvestPlanStaffMember>> ListOfEmployeeAsync(Guid employeeId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    void Add(HarvestPlanStaffMember row);

    void Remove(HarvestPlanStaffMember row);
}
