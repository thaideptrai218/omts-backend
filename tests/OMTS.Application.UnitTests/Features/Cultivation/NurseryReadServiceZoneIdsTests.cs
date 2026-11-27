using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.UnitTests.Features.Authorization;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The zones a nursery list is asked for: the chosen ones the caller may read are what reaches the database, and none is never every zone.</summary>
public sealed class NurseryReadServiceZoneIdsTests
{
    private readonly Guid zoneA = Guid.NewGuid();
    private readonly Guid zoneB = Guid.NewGuid();
    private readonly Guid zoneC = Guid.NewGuid();

    private sealed class Captured
    {
        public NurseryBatchFilter? Filter { get; set; }
    }

    private static NurseryReadService Service(Captured captured, AccountAccess access)
    {
        var nurseries = StubProxy.Create<INurseryBatchRepository>(h =>
        {
            h["SearchAsync"] = args =>
            {
                captured.Filter = (NurseryBatchFilter)args[0]!;
                return ((IReadOnlyList<NurseryBatch>)[], 0, new NurseryListTotals(0, 0, 0, 0, 0, 0));
            };
            h["SeededByBatchAsync"] = _ => (IReadOnlyDictionary<Guid, decimal>)new Dictionary<Guid, decimal>();
        });
        // No row comes back, so the views never reach their collaborators.
        var views = new NurseryBatchViews(null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        return new NurseryReadService(nurseries, null!, null!, views, ZoneAccessKit.Guard(access));
    }

    private static AccountAccess Viewer(params Guid[] zones) => ZoneAccessKit.Viewer(PermissionFunctions.CultivationCycles, zones);

    [Fact]
    public async Task The_owner_gets_exactly_the_chosen_zones()
    {
        var captured = new Captured();

        var result = await Service(captured, ZoneAccessKit.Owner()).SearchAsync(new NurseryListQuery(ZoneIds: [zoneA, zoneB, zoneA]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(captured.Filter!.ZoneId);
        Assert.True(new HashSet<Guid> { zoneA, zoneB }.SetEquals(captured.Filter.ZoneIds!));
    }

    [Fact]
    public async Task The_single_zone_and_the_zone_ids_are_joined()
    {
        var captured = new Captured();

        await Service(captured, ZoneAccessKit.Owner()).SearchAsync(new NurseryListQuery(ZoneId: zoneC, ZoneIds: [zoneA]), CancellationToken.None);

        Assert.True(new HashSet<Guid> { zoneA, zoneC }.SetEquals(captured.Filter!.ZoneIds!));
    }

    [Fact]
    public async Task A_zone_the_caller_may_not_read_is_left_out_of_zone_ids_without_a_refusal()
    {
        var captured = new Captured();

        var result = await Service(captured, Viewer(zoneA, zoneB)).SearchAsync(new NurseryListQuery(ZoneIds: [zoneB, zoneC]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([zoneB], captured.Filter!.ZoneIds!);
    }

    [Fact]
    public async Task Zone_ids_none_of_which_the_caller_may_read_keep_no_zone_never_every_zone()
    {
        var captured = new Captured();

        var result = await Service(captured, Viewer(zoneA)).SearchAsync(new NurseryListQuery(ZoneIds: [zoneB, zoneC]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured.Filter!.ZoneIds);
        Assert.Empty(captured.Filter.ZoneIds);
    }

    [Fact]
    public async Task Without_zone_ids_the_list_is_as_it_was()
    {
        var all = new Captured();
        var some = new Captured();

        await Service(all, ZoneAccessKit.Owner()).SearchAsync(new NurseryListQuery(), CancellationToken.None);
        await Service(some, Viewer(zoneA)).SearchAsync(new NurseryListQuery(), CancellationToken.None);

        Assert.Null(all.Filter!.ZoneIds);
        Assert.Equal([zoneA], some.Filter!.ZoneIds!);
    }

    [Fact]
    public async Task The_single_zone_the_caller_may_not_read_is_still_refused()
    {
        var result = await Service(new Captured(), Viewer(zoneA)).SearchAsync(new NurseryListQuery(ZoneId: zoneB, ZoneIds: [zoneA]), CancellationToken.None);

        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public async Task More_than_two_hundred_zone_ids_are_refused()
    {
        var many = Enumerable.Range(0, PermittedZones.MaxChosen + 1).Select(_ => Guid.NewGuid()).ToArray();

        var result = await Service(new Captured(), ZoneAccessKit.Owner()).SearchAsync(new NurseryListQuery(ZoneIds: many), CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("ZoneIds", result.Error.Details!["field"]);
    }
}
