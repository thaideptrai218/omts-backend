using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Returns;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The sales return list: the zones the caller may view, the validation of the query, the filters and the order handed to the database, and the shape of a row.</summary>
public sealed class SalesReturnListServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly Guid zoneA = Guid.NewGuid();
    private readonly Guid zoneB = Guid.NewGuid();

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private sealed class FakeReturns(IReadOnlyList<SalesReturnListRow> rows) : ISalesReturnRepository
    {
        public SalesReturnFilter? Filter { get; private set; }
        public (SalesReturnSort Sort, bool Descending, int Page, int PageSize)? Paging { get; private set; }

        public void Add(SalesReturn salesReturn) => throw new NotSupportedException();

        public Task<SalesReturn?> GetForEditAsync(Guid returnId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SalesReturn?> GetDetailAsync(Guid returnId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(IReadOnlyList<SalesReturnListRow> Rows, int Total, SalesReturnSums Totals)> SearchAsync(
            SalesReturnFilter filter, SalesReturnSort sort, bool descending, int page, int pageSize, CancellationToken cancellationToken)
        {
            Filter = filter;
            Paging = (sort, descending, page, pageSize);
            return Task.FromResult((rows, 42, new SalesReturnSums(1_000m, 60m, 5m)));
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<SalesReturnOfOrder>>> OfOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ZoneAccessGuard Guard(AccountAccess? access)
    {
        var accountId = access?.AccountId;
        var cache = new PermissionCache(new MemoryCache(new MemoryCacheOptions()));
        if (accountId is { } id)
        {
            cache.Set(cache.KeyFor(id), access!);
        }

        var permissions = new PermissionService(StubProxy.Create<IAccountRepository>(_ => { }), StubProxy.Create<IAccessGrantRepository>(_ => { }), StubProxy.Create<IFarmingZoneRepository>(_ => { }), cache);
        return new ZoneAccessGuard(new Fakes.FakeCurrentUser(accountId), permissions);
    }

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    // Since 20/11/2026 no fixed role holds SALES_RETURNS but the Owner's (the Zone Manager lost it), so the zone limit is read with a grant that holds only that right.
    private static AccountAccess Viewer(params Guid[] zones) => new(
        Guid.NewGuid(), SubjectType.Staff, false, true,
        zones.Select(z => new ZoneGrant(z, Guid.NewGuid(), "CUSTOM", "Returns viewer", [GrantedPermission.Of(PermissionFunctions.SalesReturns, [PermissionAction.View])])).ToList());

    private static SalesReturnListRow Row(Guid zoneId, Guid? orderId, Guid? customerId, Guid recordedBy) => new(
        Guid.NewGuid(), "TL000002", new DateOnly(2026, 11, 17), At, Guid.NewGuid(), "HD000009", orderId, orderId is null ? null : "DH000004", customerId, customerId is null ? null : "KH000001",
        customerId is null ? null : "Chi Hoa", zoneId, "KV01", 3_500_000m, StatusIds.SalesReturn.Recorded, recordedBy, 3, 40m, 5m);

    private SalesReturnListService Service(FakeReturns returns, AccountAccess? access, params (Guid Id, string Name)[] names) => new(
        returns,
        StubProxy.Create<IAccountRepository>(h => h["GetDisplayNamesAsync"] = _ => (IReadOnlyDictionary<Guid, string>)names.ToDictionary(n => n.Id, n => n.Name)),
        Guard(access),
        new Catalog());

    [Fact]
    public async Task The_owner_reads_every_zone_and_the_filters_reach_the_database_as_asked()
    {
        var returns = new FakeReturns([]);
        var customer = Guid.NewGuid();
        var query = new SalesReturnListQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), customer, zoneB, StatusIds.SalesReturn.Cancelled, "TL0", 2, 50, "value", "asc");

        var result = await Service(returns, Owner()).SearchAsync(query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(returns.Filter!.ZoneId);
        Assert.Equal([zoneB], returns.Filter.ZoneIds!);
        Assert.Equal(
            (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), customer, StatusIds.SalesReturn.Cancelled, "TL0"),
            (returns.Filter.From, returns.Filter.To, returns.Filter.CustomerId, returns.Filter.StatusId, returns.Filter.Search));
        Assert.Equal((SalesReturnSort.Value, false, 2, 50), returns.Paging);
    }

    [Fact]
    public async Task Zone_ids_keep_the_chosen_zones_the_caller_may_view_and_none_left_is_no_zone_never_every_zone()
    {
        var returns = new FakeReturns([]);
        var manager = Viewer(zoneA);

        var partly = await Service(returns, manager).SearchAsync(new SalesReturnListQuery(ZoneIds: [zoneA, zoneB]), CancellationToken.None);
        var keptPartly = returns.Filter!.ZoneIds!.ToArray();
        var none = await Service(returns, manager).SearchAsync(new SalesReturnListQuery(ZoneIds: [zoneB]), CancellationToken.None);
        var keptNone = returns.Filter!.ZoneIds!.ToArray();
        var joined = await Service(returns, Owner()).SearchAsync(new SalesReturnListQuery(ZoneId: zoneA, ZoneIds: [zoneB, zoneA]), CancellationToken.None);

        Assert.True(partly.IsSuccess);
        Assert.Equal([zoneA], keptPartly);
        Assert.True(none.IsSuccess);
        Assert.Empty(keptNone);
        Assert.True(joined.IsSuccess);
        Assert.True(new HashSet<Guid> { zoneA, zoneB }.SetEquals(returns.Filter!.ZoneIds!));
    }

    [Fact]
    public async Task More_than_two_hundred_zone_ids_are_refused()
    {
        var many = Enumerable.Range(0, PermittedZones.MaxChosen + 1).Select(_ => Guid.NewGuid()).ToArray();

        var result = await Service(new FakeReturns([]), Owner()).SearchAsync(new SalesReturnListQuery(ZoneIds: many), CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
    }

    [Fact]
    public async Task A_staff_caller_is_limited_to_the_zones_they_may_view_and_is_refused_another_zone()
    {
        var returns = new FakeReturns([]);
        var manager = Viewer(zoneA);

        var own = await Service(returns, manager).SearchAsync(new SalesReturnListQuery(), CancellationToken.None);
        var chosenOther = await Service(returns, manager).SearchAsync(new SalesReturnListQuery(ZoneId: zoneB), CancellationToken.None);

        Assert.True(own.IsSuccess);
        Assert.Equal([zoneA], returns.Filter!.ZoneIds!.ToArray());
        Assert.Equal(ErrorKind.Forbidden, chosenOther.Error!.Kind);
    }

    [Fact]
    public async Task A_caller_with_no_access_to_sales_returns_is_refused()
    {
        var result = await Service(new FakeReturns([]), null).SearchAsync(new SalesReturnListQuery(), CancellationToken.None);

        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Theory]
    [InlineData("2026-12-01", "2026-11-01", null, null)]
    [InlineData(null, null, "customer", null)]
    [InlineData(null, null, null, "up")]
    public async Task An_upside_down_period_an_unknown_sort_and_an_unknown_direction_are_invalid_fields(string? from, string? to, string? sort, string? direction)
    {
        var query = new SalesReturnListQuery(from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), Sort: sort, SortDir: direction);

        var result = await Service(new FakeReturns([]), Owner()).SearchAsync(query, CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
    }

    [Fact]
    public async Task The_default_order_is_the_code_descending_and_the_page_size_is_capped()
    {
        var returns = new FakeReturns([]);

        await Service(returns, Owner()).SearchAsync(new SalesReturnListQuery(Page: 0, PageSize: 5_000), CancellationToken.None);

        Assert.Equal((SalesReturnSort.Code, true, 1, 100), returns.Paging);
    }

    [Fact]
    public async Task A_row_carries_its_links_names_kg_and_the_totals_come_with_the_page()
    {
        var zone = zoneA;
        var order = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var by = Guid.NewGuid();
        var returns = new FakeReturns([Row(zone, order, customer, by), Row(zone, null, null, by)]);

        var result = await Service(returns, Owner(), (by, "Anh Tu")).SearchAsync(new SalesReturnListQuery(), CancellationToken.None);

        var page = result.Value!;
        Assert.Equal((42, 1, 20), (page.Total, page.Page, page.PageSize));
        Assert.Equal((1_000m, 60m, 5m), (page.Totals.Value, page.Totals.BackForSaleKg, page.Totals.DiscardedKg));
        var first = page.Items[0];
        Assert.Equal(("DH000004", "KH000001", "Chi Hoa", "KV01", "Anh Tu", 3, 40m, 5m), (first.SalesOrder!.Code, first.Customer!.Code, first.Customer.Name, first.ZoneCode, first.RecordedByName, first.LineCount, first.BackForSaleKg, first.DiscardedKg));
        Assert.Equal(("Status 4101", At), (first.Status, first.RecordedAt));
        Assert.Null(page.Items[1].SalesOrder);
        Assert.Null(page.Items[1].Customer);
    }
}
