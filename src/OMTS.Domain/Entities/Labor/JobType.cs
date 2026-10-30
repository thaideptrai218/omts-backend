using OMTS.Domain.Common;
using OMTS.Domain.Constants;
namespace OMTS.Domain.Entities.Labor;

/// <summary>LABOR-02: a job catalogue, independent of rates and payroll.</summary>
public class JobType : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
}
