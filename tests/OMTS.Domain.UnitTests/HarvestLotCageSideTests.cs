using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Domain.UnitTests;

/// <summary>The cooperative's and the trader's cages are rows of one table; the lot reads each side on its own so the trader's cages never count in the weighing.</summary>
public class HarvestLotCageSideTests
{
    [Fact]
    public void The_lot_reads_each_side_of_its_cages_apart()
    {
        var lot = new HarvestLot();
        lot.AllCages.Add(new HarvestLotCage { Number = 1, Kg = 10m });
        lot.AllCages.Add(new HarvestLotCage { Number = 1, Kg = 10.5m, Side = HarvestLotCageSide.Trader });
        lot.AllCages.Add(new HarvestLotCage { Number = 2, Kg = 11m, Side = HarvestLotCageSide.Cooperative });

        Assert.Equal([10m, 11m], lot.Cages.Select(c => c.Kg));
        Assert.Equal([10.5m], lot.TraderCages.Select(c => c.Kg));
    }

    [Fact]
    public void A_new_cage_is_the_cooperatives_and_a_lot_without_cages_has_no_cages_on_either_side()
    {
        Assert.Equal(HarvestLotCageSide.Cooperative, new HarvestLotCage().Side);
        var lot = new HarvestLot();
        Assert.Empty(lot.Cages);
        Assert.Empty(lot.TraderCages);
    }

    [Fact]
    public void A_cage_taken_off_the_lot_leaves_its_side_only()
    {
        var lot = new HarvestLot();
        var trader = new HarvestLotCage { Number = 1, Kg = 9m, Side = HarvestLotCageSide.Trader };
        lot.AllCages.Add(new HarvestLotCage { Number = 1, Kg = 10m });
        lot.AllCages.Add(trader);

        lot.AllCages.Remove(trader);

        Assert.Single(lot.Cages);
        Assert.Empty(lot.TraderCages);
    }
}
