using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// An employee who worked on a harvest trip (decision of 12/11/2026). The list is for the record only: no quota per person, no link to orders. One employee may be on the lots of two rafts
/// on the same day.
/// </summary>
public class HarvestLotCrewMember
{
    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
}
