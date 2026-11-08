using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>BR-TRACE-01 (what a lot misses for the provenance page) and TRACE-01 PRE-2 with BR-TRACE-02 (who may publish which lot, from the Trace lot screen).</summary>
public sealed class ProvenancePublishRulesTests
{
    private static readonly Guid Lot = Guid.NewGuid(), Zone = Guid.NewGuid(), OtherZone = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 11, 8, 2, 0, 0, TimeSpan.Zero);

    private static LotLaneSeedLot Row(int lane, string? seedLot = "PN000001-1", bool completed = true) => new(Lot, lane, seedLot, completed);

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    private static AccountAccess Staff(FixedRole role, Guid? zone) =>
        new(Guid.NewGuid(), SubjectType.Staff, false, true, [new ZoneGrant(zone, Guid.NewGuid(), role.Code, role.Name, role.GrantedPermissions)]);

    private static HarvestLot InState(HarvestLotOriginState state) => state switch
    {
        HarvestLotOriginState.Published => new HarvestLot { PublishedAt = At },
        HarvestLotOriginState.Withdrawn => new HarvestLot { PublishedAt = At, WithdrawnAt = At.AddDays(1) },
        _ => new HarvestLot(),
    };

    [Fact]
    public void A_lot_whose_every_lane_has_a_seed_lot_of_a_completed_receipt_misses_nothing()
    {
        // Lane 2 was hung again: one seed lot on a cancelled receipt is enough as long as another one is on a completed receipt (BR-CYCLE-02).
        Assert.Empty(ProvenanceReadiness.Missing([Row(1), Row(2, "PN000002-1", completed: false), Row(2, "PN000003-1")]));
    }

    [Fact]
    public void Each_lane_without_a_seed_lot_is_named_in_lane_order_and_never_filled_in()
    {
        var missing = ProvenanceReadiness.Missing([Row(3, "PN000004-2", completed: false), Row(3, "PN000004-1", completed: false), Row(1), Row(2, seedLot: null)]);

        Assert.Equal(
            ["lane 2 has no seeding session in the cycle of the lot", "the seed lot PN000004-1, PN000004-2 of lane 3 is not on a completed purchase receipt"],
            missing.Select(m => m.Describe()));
        Assert.Equal([(OriginGapKind.NoSeedingSession, (int?)2), (OriginGapKind.SeedLotNotOnCompletedReceipt, 3)], missing.Select(m => (m.Kind, m.LaneNumber)));
        Assert.Equal(["PN000004-1", "PN000004-2"], missing[1].SeedLotCodes);
    }

    [Theory]
    [InlineData(HarvestLotOriginState.NotPublished, true, null)]
    [InlineData(HarvestLotOriginState.NotPublished, false, LotPublishBlock.NotOnSalesInvoice)]
    [InlineData(HarvestLotOriginState.Published, true, LotPublishBlock.AlreadyPublished)]
    [InlineData(HarvestLotOriginState.Withdrawn, true, LotPublishBlock.OwnerOnly)]
    public void A_zone_manager_publishes_a_not_published_lot_on_an_invoice_of_the_zone_only(HarvestLotOriginState state, bool onInvoice, LotPublishBlock? expected)
    {
        Assert.Equal(expected, LotPublishRules.Block(Staff(FixedRoles.ZoneManager, Zone), InState(state), Zone, onInvoice));
        Assert.Equal(expected, LotPublishRules.Block(Staff(FixedRoles.ZoneManager, null), InState(state), Zone, onInvoice));
        Assert.Equal(LotPublishBlock.NoRight, LotPublishRules.Block(Staff(FixedRoles.ZoneManager, OtherZone), InState(state), Zone, onInvoice));
    }

    [Theory]
    [InlineData(HarvestLotOriginState.NotPublished, true, null)]
    [InlineData(HarvestLotOriginState.NotPublished, false, LotPublishBlock.NotOnSalesInvoice)]
    [InlineData(HarvestLotOriginState.Published, true, LotPublishBlock.AlreadyPublished)]
    [InlineData(HarvestLotOriginState.Withdrawn, false, null)]
    public void The_owner_also_publishes_a_withdrawn_lot_again_and_the_accountant_publishes_nothing(HarvestLotOriginState state, bool onInvoice, LotPublishBlock? expected)
    {
        Assert.Equal(expected, LotPublishRules.Block(Owner(), InState(state), Zone, onInvoice));
        Assert.Equal(LotPublishBlock.NoRight, LotPublishRules.Block(Staff(FixedRoles.Accountant, null), InState(state), Zone, onInvoice));
        Assert.Equal(LotPublishBlock.NoRight, LotPublishRules.Block(null, InState(state), Zone, onInvoice));
    }
}
