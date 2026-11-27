using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.CultivationLogs;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The cross-raft log list: which zones the caller may read, and how the filters and the page are read.</summary>
public class CultivationLogListFiltersTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();
    private static readonly PermittedZones OnlyA = new(false, new HashSet<Guid> { ZoneA });

    private static AppError? Read(CultivationLogListQuery query, PermittedZones allowed, out CultivationLogFilter filter, out int page, out int pageSize) =>
        CultivationLogListFilters.Read(query, allowed, out filter, out page, out pageSize);

    [Fact]
    public void A_caller_who_reads_no_zone_is_refused_even_with_no_filter()
    {
        var error = Read(new CultivationLogListQuery(), PermittedZones.None, out _, out _, out _);

        Assert.Equal(ErrorKind.Forbidden, error!.Kind);
    }

    [Fact]
    public void A_zone_the_caller_cannot_read_is_refused()
    {
        var error = Read(new CultivationLogListQuery(ZoneId: ZoneB), OnlyA, out _, out _, out _);

        Assert.Equal(ErrorKind.Forbidden, error!.Kind);
    }

    [Fact]
    public void A_zone_the_caller_reads_is_filtered_and_the_others_are_not_asked_for()
    {
        Assert.Null(Read(new CultivationLogListQuery(ZoneId: ZoneA), OnlyA, out var filter, out _, out _));

        Assert.Equal(ZoneA, filter.ZoneId);
        Assert.Equal([ZoneA], filter.ZoneIds!);
    }

    [Fact]
    public void With_no_zone_asked_the_zone_scoped_caller_sees_only_their_zones()
    {
        Assert.Null(Read(new CultivationLogListQuery(), OnlyA, out var filter, out _, out _));

        Assert.Null(filter.ZoneId);
        Assert.Equal([ZoneA], filter.ZoneIds!);
    }

    [Fact]
    public void A_caller_who_reads_every_zone_has_no_zone_limit_and_may_name_any_zone()
    {
        Assert.Null(Read(new CultivationLogListQuery(ZoneId: ZoneB), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Null(filter.ZoneIds);
        Assert.Equal(ZoneB, filter.ZoneId);
    }

    [Fact]
    public void The_filters_are_read_as_sent()
    {
        var raft = Guid.NewGuid();
        var query = new CultivationLogListQuery(null, raft, " repair ", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 27), "  B01 ");

        Assert.Null(Read(query, PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Equal(raft, filter.RaftId);
        Assert.Equal("REPAIR", filter.LogType);
        Assert.Equal(new DateOnly(2026, 11, 1), filter.From);
        Assert.Equal(new DateOnly(2026, 11, 27), filter.To);
        Assert.Equal("B01", filter.Search);
    }

    [Fact]
    public void A_blank_type_and_a_blank_search_filter_nothing()
    {
        Assert.Null(Read(new CultivationLogListQuery(LogType: " ", Search: "   "), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Null(filter.LogType);
        Assert.Null(filter.Search);
    }

    [Fact]
    public void A_wildcard_in_the_search_is_kept_as_text()
    {
        Assert.Null(Read(new CultivationLogListQuery(Search: "100%_a"), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Equal("100%_a", filter.Search);
    }

    [Fact]
    public void An_unknown_type_is_msg_gen_01_on_log_type()
    {
        var error = Read(new CultivationLogListQuery(LogType: "SWIMMING"), PermittedZones.Everywhere, out _, out _, out _);

        Assert.Equal(ErrorKind.Validation, error!.Kind);
        Assert.Equal("LogType", error.Details!["field"]);
    }

    [Fact]
    public void A_refused_zone_comes_before_an_invalid_filter()
    {
        var error = Read(new CultivationLogListQuery(ZoneId: ZoneB, LogType: "SWIMMING"), OnlyA, out _, out _, out _);

        Assert.Equal(ErrorKind.Forbidden, error!.Kind);
    }

    [Fact]
    public void A_period_that_ends_before_it_starts_is_refused_and_one_day_is_fine()
    {
        var day = new DateOnly(2026, 11, 20);

        var error = Read(new CultivationLogListQuery(From: day, To: day.AddDays(-1)), PermittedZones.Everywhere, out _, out _, out _);
        Assert.Equal("To", error!.Details!["field"]);

        Assert.Null(Read(new CultivationLogListQuery(From: day, To: day), PermittedZones.Everywhere, out _, out _, out _));
        Assert.Null(Read(new CultivationLogListQuery(To: day), PermittedZones.Everywhere, out _, out _, out _));
    }

    [Fact]
    public void A_search_longer_than_100_characters_is_refused()
    {
        var error = Read(new CultivationLogListQuery(Search: new string('a', 101)), PermittedZones.Everywhere, out _, out _, out _);

        Assert.Equal("Search", error!.Details!["field"]);
        Assert.Null(Read(new CultivationLogListQuery(Search: new string('a', 100)), PermittedZones.Everywhere, out _, out _, out _));
    }

    [Theory]
    [InlineData(1, 20, 1, 20)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, -1, 1, 1)]
    [InlineData(3, 500, 3, 100)]
    [InlineData(2, 100, 2, 100)]
    public void The_page_is_clamped(int page, int pageSize, int expectedPage, int expectedSize)
    {
        Assert.Null(Read(new CultivationLogListQuery(Page: page, PageSize: pageSize), PermittedZones.Everywhere, out _, out var readPage, out var readSize));

        Assert.Equal((expectedPage, expectedSize), (readPage, readSize));
    }
}
