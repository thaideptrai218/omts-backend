using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Common;

/// <summary>
/// An employee named on a nursery, seeding or harvest record (decision of 17/11/2026): a row of the record's employee table, for the record only. The three tables hold nothing but the
/// record id and the employee id, so the crew rules are written once for all of them.
/// </summary>
public interface IRecordEmployee
{
    Guid EmployeeId { get; set; }
    Employee Employee { get; set; }
}
