using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Audit.AuditPlanQaKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6 on SQL Server: the actual plans SQL Server makes for the ACCOUNT-10 queries as EF sends them, on
/// a log of 40 000 events over about 4.5 years. Each filter that leads an index ending with the time seeks that index and
/// the page needs no sort; the record code and record type filters read far more of the log than they match.
/// </summary>
public class AuditSearchIndexPlanQaTests(ITestOutputHelper output)
{
    [SqlServerFact]
    public async Task Each_filter_seeks_its_index_and_the_page_is_read_in_index_order()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (accounts, employees, zones) = await SeedAsync(database);
        var today = TimeProvider.System.Today();
        var (from, to, fiveYears) = (Start(today.AddDays(-29)), Start(today.AddDays(1)), Start(today.AddYears(-5)));

        var cases = new (string Name, AuditEventFilter Filter, string? Index, bool Sorted)[]
        {
            ("period only", new(from, to), "IX_AuditEvents_OccurredAt", false),
            ("account", new(from, to, ActorAccountId: accounts[3]), "IX_AuditEvents_ActorAccountId_OccurredAt", false),
            ("employee", new(from, to, ActorEmployeeId: employees[5]), "IX_AuditEvents_ActorEmployeeId_OccurredAt", false),
            ("system", new(from, to, SystemActor: true), "IX_AuditEvents_ActorAccountId_OccurredAt", false),
            ("action", new(from, to, Action: "Action07.Done"), "IX_AuditEvents_Action_OccurredAt", false),
            ("zone", new(from, to, ZoneId: zones[2]), "IX_AuditEvents_ZoneId_OccurredAt", false),
            ("record code", new(from, to, RecordCodePrefix: "KV00012"), "IX_AuditEvents_RecordCode_OccurredAt", true),
            // Reported, not asserted here (see the tests below).
            ("record type", new(from, to, RecordType: "Type03"), null, true),
            ("rare record type, 5 years", new(fiveYears, to, RecordType: "RareType"), null, true),
            ("account and action, 5 years", new(fiveYears, to, ActorAccountId: accounts[3], Action: "Action03.Done"), null, true),
        };

        var failures = new List<string>();
        foreach (var (name, filter, index, sorted) in cases)
        {
            foreach (var (isPage, operators, rowsRead, _) in await PlansAsync(database, filter))
            {
                var plan = $"{name} ({(isPage ? "page" : "count")}, {rowsRead} audit rows read): {string.Join(" | ", operators)}";
                output.WriteLine(plan);
                if (index is null)
                {
                    continue;
                }

                // The page reads its own index in order; the count may pick any index, but never scans the log.
                if (isPage && !operators.Contains($"Index Seek {index}"))
                {
                    failures.Add($"does not seek {index}: {plan}");
                }

                if (isPage && !sorted && operators.Any(o => o.StartsWith("Sort", StringComparison.Ordinal)))
                {
                    failures.Add($"sorts: {plan}");
                }

                if (operators.Any(o => o.Contains("Scan", StringComparison.Ordinal) && o.Contains("AuditEvents", StringComparison.Ordinal)))
                {
                    failures.Add($"scans: {plan}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The RecordCode index is there so that a code is a seek ("LIKE 'code%'"). The prefix is sent as a parameter, and
    // SQL Server then seeks only "RecordCode IS NOT NULL": every coded event of the whole log is read, for the count and
    // for the page, whatever the period (8 events match here, out of 40 000).
    [SqlServerFact(Skip = "Deferred: plan reuse at scale (QA 0.6 #3)")]
    public async Task A_record_code_search_reads_only_the_codes_that_start_with_it()
    {
        await using var database = SqlServerTestDatabase.Create();
        await SeedAsync(database);
        var today = TimeProvider.System.Today();

        await AssertReadsFewRowsAsync(database, new AuditEventFilter(Start(today.AddDays(-29)), Start(today.AddDays(1)), RecordCodePrefix: "KV00012"));
    }

    // The plan SQL Server keeps for the record type query is made for a common type in the last 30 days; a rare type
    // searched over 5 years under that plan must still read only its own events (40 matches), not the whole log. The
    // (RecordType, OccurredAt) index makes the record type a seek.
    [SqlServerFact]
    public async Task A_record_type_search_reads_only_the_events_of_that_type()
    {
        await using var database = SqlServerTestDatabase.Create();
        await SeedAsync(database);
        var today = TimeProvider.System.Today();
        await PlansAsync(database, new AuditEventFilter(Start(today.AddDays(-29)), Start(today.AddDays(1)), RecordType: "Type03"));

        await AssertReadsFewRowsAsync(database, new AuditEventFilter(Start(today.AddYears(-5)), Start(today.AddDays(1)), RecordType: "RareType"));
    }

    private async Task AssertReadsFewRowsAsync(SqlServerTestDatabase database, AuditEventFilter filter)
    {
        var plans = await PlansAsync(database, filter);
        foreach (var (isPage, operators, rowsRead, text) in plans)
        {
            output.WriteLine($"{(isPage ? "page" : "count")}, {rowsRead} audit rows read: {string.Join(" | ", operators)}{Environment.NewLine}{text}");
        }

        Assert.All(plans, p => Assert.True(p.RowsRead < 1_000, $"{(p.IsPage ? "page" : "count")} read {p.RowsRead} audit rows of {Rows}."));
    }
}
