using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>An employee who worked on a seeding session (decision of 17/11/2026): for the record only, like the crew of a harvest lot.</summary>
public class SeedingRecordEmployee : IRecordEmployee
{
    public Guid SeedingRecordId { get; set; }
    public SeedingRecord SeedingRecord { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
}
