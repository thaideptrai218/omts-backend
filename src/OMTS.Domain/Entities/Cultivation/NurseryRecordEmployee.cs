using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>An employee who worked on a nursery batch (decision of 17/11/2026): for the record only, like the crew of a harvest lot.</summary>
public class NurseryRecordEmployee : IRecordEmployee
{
    public Guid NurseryBatchId { get; set; }
    public NurseryBatch NurseryBatch { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
}
