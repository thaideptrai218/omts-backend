using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.UnitTests.Features.Authorization;
using OMTS.Application.UnitTests.Features.Purchasing;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The zones an invoice list is asked for: the chosen ones the caller may read are what reaches the database, and none is never every zone.</summary>
public sealed class SalesInvoiceSearchServiceZoneIdsTests
{
    private readonly Guid zoneA = Guid.NewGuid();
    private readonly Guid zoneB = Guid.NewGuid();
    private readonly Guid zoneC = Guid.NewGuid();

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private static SalesInvoiceSearchService Service(Action<SalesInvoiceSearchFilter> capture, AccountAccess access)
    {
        var reader = StubProxy.Create<ISalesInvoiceSearchReader>(h =>
        {
            h["MatchAsync"] = args =>
            {
                capture((SalesInvoiceSearchFilter)args[0]!);
                return new SalesInvoiceMatchPage(0, 0m, []);
            };
            h["RowsAsync"] = _ => (IReadOnlyList<SalesInvoiceSearchRow>)[];
            h["LatestSendingsAsync"] = _ => (IReadOnlyDictionary<Guid, SalesInvoiceLatestSending>)new Dictionary<Guid, SalesInvoiceLatestSending>();
        });
        // A search without the paid filter reads the debts of the customers of the page, and the page has none.
        var debts = new CustomerDebtReader(null!, null!);
        return new SalesInvoiceSearchService(reader, debts, ZoneAccessKit.Guard(access), new Fakes.FakeCurrentUser(access.AccountId), new Catalog(), TimeProvider.System);
    }

    private static AccountAccess Viewer(params Guid[] zones) => ZoneAccessKit.Viewer(PermissionFunctions.SalesInvoices, zones);

    [Fact]
    public async Task The_owner_gets_exactly_the_chosen_zones()
    {
        SalesInvoiceSearchFilter? filter = null;

        var result = await Service(f => filter = f, ZoneAccessKit.Owner()).SearchAsync(new SalesInvoiceSearchQuery(ZoneIds: [zoneA, zoneB, zoneA]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(new HashSet<Guid> { zoneA, zoneB }.SetEquals(filter!.ZoneIds!));
    }

    [Fact]
    public async Task The_single_zone_and_the_zone_ids_are_joined()
    {
        SalesInvoiceSearchFilter? filter = null;

        await Service(f => filter = f, ZoneAccessKit.Owner()).SearchAsync(new SalesInvoiceSearchQuery(ZoneId: zoneC, ZoneIds: [zoneA]), CancellationToken.None);

        Assert.True(new HashSet<Guid> { zoneA, zoneC }.SetEquals(filter!.ZoneIds!));
    }

    [Fact]
    public async Task A_zone_the_caller_may_not_read_is_left_out_of_zone_ids_without_a_refusal()
    {
        SalesInvoiceSearchFilter? filter = null;

        var result = await Service(f => filter = f, Viewer(zoneA, zoneB)).SearchAsync(new SalesInvoiceSearchQuery(ZoneIds: [zoneB, zoneC]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([zoneB], filter!.ZoneIds!);
    }

    [Fact]
    public async Task Zone_ids_none_of_which_the_caller_may_read_keep_no_zone_never_every_zone()
    {
        SalesInvoiceSearchFilter? filter = null;

        var result = await Service(f => filter = f, Viewer(zoneA)).SearchAsync(new SalesInvoiceSearchQuery(ZoneIds: [zoneB, zoneC]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(filter!.ZoneIds);
        Assert.Empty(filter.ZoneIds);
    }

    [Fact]
    public async Task Without_zone_ids_the_list_is_as_it_was()
    {
        SalesInvoiceSearchFilter? all = null;
        SalesInvoiceSearchFilter? some = null;
        SalesInvoiceSearchFilter? one = null;

        await Service(f => all = f, ZoneAccessKit.Owner()).SearchAsync(new SalesInvoiceSearchQuery(), CancellationToken.None);
        await Service(f => some = f, Viewer(zoneA, zoneB)).SearchAsync(new SalesInvoiceSearchQuery(), CancellationToken.None);
        await Service(f => one = f, Viewer(zoneA, zoneB)).SearchAsync(new SalesInvoiceSearchQuery(ZoneId: zoneB), CancellationToken.None);

        Assert.Null(all!.ZoneIds);
        Assert.True(new HashSet<Guid> { zoneA, zoneB }.SetEquals(some!.ZoneIds!));
        Assert.Equal([zoneB], one!.ZoneIds!);
    }

    [Fact]
    public async Task The_single_zone_the_caller_may_not_read_is_still_refused()
    {
        var result = await Service(_ => { }, Viewer(zoneA)).SearchAsync(new SalesInvoiceSearchQuery(ZoneId: zoneB, ZoneIds: [zoneA]), CancellationToken.None);

        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public async Task More_than_two_hundred_zone_ids_are_refused()
    {
        var many = Enumerable.Range(0, PermittedZones.MaxChosen + 1).Select(_ => Guid.NewGuid()).ToArray();

        var result = await Service(_ => { }, ZoneAccessKit.Owner()).SearchAsync(new SalesInvoiceSearchQuery(ZoneIds: many), CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("ZoneIds", result.Error.Details!["field"]);
    }
}
