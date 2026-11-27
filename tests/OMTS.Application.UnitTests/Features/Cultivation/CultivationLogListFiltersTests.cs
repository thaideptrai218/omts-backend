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

        Assert.Null(filter.ZoneId);
        Assert.Equal([ZoneA], filter.ZoneIds!);
    }

    [Fact]
    public void Zone_ids_keep_the_zones_the_caller_reads_and_leave_the_others_out_without_a_refusal()
    {
        Assert.Null(Read(new CultivationLogListQuery(ZoneIds: [ZoneA, ZoneB]), OnlyA, out var filter, out _, out _));

        Assert.Equal([ZoneA], filter.ZoneIds!);
    }

    [Fact]
    public void Zone_ids_none_of_which_the_caller_reads_keep_no_zone_never_every_zone()
    {
        Assert.Null(Read(new CultivationLogListQuery(ZoneIds: [ZoneB]), OnlyA, out var filter, out _, out _));

        Assert.NotNull(filter.ZoneIds);
        Assert.Empty(filter.ZoneIds);
    }

    [Fact]
    public void The_single_zone_and_the_zone_ids_are_joined_and_the_single_zone_is_still_refused_when_not_read()
    {
        Assert.Null(Read(new CultivationLogListQuery(ZoneId: ZoneA, ZoneIds: [ZoneA]), PermittedZones.Everywhere, out var filter, out _, out _));
        var error = Read(new CultivationLogListQuery(ZoneId: ZoneB, ZoneIds: [ZoneA]), OnlyA, out _, out _, out _);

        Assert.Equal([ZoneA], filter.ZoneIds!);
        Assert.Equal(ErrorKind.Forbidden, error!.Kind);
    }

    [Fact]
    public void More_than_two_hundred_zone_ids_are_refused()
    {
        var many = Enumerable.Range(0, PermittedZones.MaxChosen + 1).Select(_ => Guid.NewGuid()).ToArray();

        var error = Read(new CultivationLogListQuery(ZoneIds: many), PermittedZones.Everywhere, out _, out _, out _);

        Assert.Equal("ZoneIds", error!.Details!["field"]);
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

        Assert.Null(filter.ZoneId);
        Assert.Equal([ZoneB], filter.ZoneIds!);
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

    [Fact]
    public void With_no_sort_the_latest_visit_comes_first()
    {
        Assert.Null(Read(new CultivationLogListQuery(), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Equal((CultivationLogSort.VisitDate, true), (filter.Sort, filter.Descending));
        Assert.Null(Read(new CultivationLogListQuery(SortBy: " ", SortDir: " "), PermittedZones.Everywhere, out filter, out _, out _));
        Assert.Equal((CultivationLogSort.VisitDate, true), (filter.Sort, filter.Descending));
    }

    [Theory]
    [InlineData("visitDate", CultivationLogSort.VisitDate)]
    [InlineData("ZONE", CultivationLogSort.Zone)]
    [InlineData(" raft ", CultivationLogSort.Raft)]
    [InlineData("logType", CultivationLogSort.LogType)]
    [InlineData("RecordedBy", CultivationLogSort.RecordedBy)]
    [InlineData("recordedat", CultivationLogSort.RecordedAt)]
    public void Every_column_of_the_list_can_be_sorted_in_any_letter_case(string sortBy, CultivationLogSort expected)
    {
        Assert.Null(Read(new CultivationLogListQuery(SortBy: sortBy), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Equal(expected, filter.Sort);
        Assert.True(filter.Descending);
    }

    [Theory]
    [InlineData("asc", false)]
    [InlineData(" ASC ", false)]
    [InlineData("desc", true)]
    [InlineData("Desc", true)]
    public void The_direction_is_read_in_any_letter_case(string sortDir, bool descending)
    {
        Assert.Null(Read(new CultivationLogListQuery(SortBy: "raft", SortDir: sortDir), PermittedZones.Everywhere, out var filter, out _, out _));

        Assert.Equal(descending, filter.Descending);
    }

    [Fact]
    public void An_unknown_sort_column_is_msg_gen_01_on_sort_by()
    {
        var error = Read(new CultivationLogListQuery(SortBy: "notes"), PermittedZones.Everywhere, out _, out _, out _);

        Assert.Equal(ErrorKind.Validation, error!.Kind);
        Assert.Equal("SortBy", error.Details!["field"]);
    }

    [Fact]
    public void An_unknown_direction_is_msg_gen_01_on_sort_dir()
    {
        var error = Read(new CultivationLogListQuery(SortBy: "zone", SortDir: "up"), PermittedZones.Everywhere, out _, out _, out _);

        Assert.Equal(ErrorKind.Validation, error!.Kind);
        Assert.Equal("SortDir", error.Details!["field"]);
    }

    [Fact]
    public void A_refused_zone_comes_before_an_invalid_sort()
    {
        var error = Read(new CultivationLogListQuery(ZoneId: ZoneB, SortBy: "notes"), OnlyA, out _, out _, out _);

        Assert.Equal(ErrorKind.Forbidden, error!.Kind);
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
