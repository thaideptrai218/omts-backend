using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, the Payroll entity of §I.3.2 as a header: the status group, what the table holds, what writes it, no route.</summary>
public class LaborSettingsQaPayrollHeaderTests
{
    [SqlServerFact]
    public async Task The_statuses_are_the_four_of_the_entity_in_their_own_group()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);

        var statuses = await QueryAsync(api, db => db.Statuses.AsNoTracking().Where(s => s.Group == StatusGroups.Payroll).OrderBy(s => s.SortOrder).ToListAsync());
        var sameIds = await QueryAsync(api, db => db.Statuses.AsNoTracking().CountAsync(s => s.Id >= 3400 && s.Id < 3500));

        Assert.Equal(
            [
                (3401, "DRAFT", "Draft", true, false),
                (3402, "AWAITING_FINALIZATION", "Awaiting finalization", false, false),
                (3403, "SENT_BACK", "Sent back", false, false),
                (3404, "FINALIZED", "Finalized", false, true),
            ],
            statuses.Select(s => (s.Id, s.Code, s.DisplayName, s.IsInitial, s.IsFinal)));
        Assert.Equal(4, sameIds);
        Assert.Equal((3401, 3402, 3403, 3404), (StatusIds.Payroll.Draft, StatusIds.Payroll.AwaitingFinalization, StatusIds.Payroll.SentBack, StatusIds.Payroll.Finalized));
        using var scope = api.Services.CreateScope();
        Assert.Equal("Awaiting finalization", scope.ServiceProvider.GetRequiredService<IStatusCatalog>().DisplayNameOf(3402));
    }

    [Theory]
    [InlineData(StatusIds.Payroll.Draft, false)]
    [InlineData(StatusIds.Payroll.AwaitingFinalization, true)]
    [InlineData(StatusIds.Payroll.SentBack, false)]
    [InlineData(StatusIds.Payroll.Finalized, true)]
    public void A_payroll_locks_while_awaiting_finalization_and_once_finalized(int statusId, bool locked) =>
        Assert.Equal(locked, new Payroll { StatusId = statusId }.IsLocked);

    [SqlServerFact]
    public async Task A_new_payroll_is_a_draft_a_one_day_period_fits_and_the_same_period_is_refused()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var payroll = new Payroll { PeriodStart = new(2026, 10, 26), PeriodEnd = new(2026, 10, 26) };

        await ChangeAsync(api, db => db.Payrolls.Add(payroll));

        var stored = await QueryAsync(api, db => db.Payrolls.AsNoTracking().SingleAsync());
        Assert.Equal((StatusIds.Payroll.Draft, false), (stored.StatusId, stored.CreatedBySystem));
        var again = await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(api, db => db.Payrolls.Add(new Payroll { PeriodStart = new(2026, 10, 26), PeriodEnd = new(2026, 10, 27) })));
        Assert.Contains("IX_Payrolls_PeriodStart", (again.InnerException as SqlException)?.Message);
    }

    // "That periods do not overlap is kept when a payroll is created (LABOR-12)" and "the status comes from the code": for L7, kept known.
    [SqlServerFact]
    public async Task The_table_alone_keeps_neither_periods_apart_nor_statuses_to_their_group()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);

        await ChangeAsync(api, db => db.Payrolls.Add(new Payroll { PeriodStart = new(2026, 10, 1), PeriodEnd = new(2026, 10, 31) }));
        await ChangeAsync(api, db => db.Payrolls.Add(new Payroll { PeriodStart = new(2026, 10, 15), PeriodEnd = new(2026, 11, 14) }));
        await ChangeAsync(api, db => db.Payrolls.Add(new Payroll { PeriodStart = new(2027, 1, 1), PeriodEnd = new(2027, 1, 31), StatusId = StatusIds.Usage.InUse }));

        Assert.Equal(3, await QueryAsync(api, db => db.Payrolls.CountAsync()));
    }

    [SqlServerFact]
    public async Task A_dated_change_moves_only_the_version_and_the_last_update_of_a_payroll_it_touches()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");
        var at = new DateTimeOffset(2020, 11, 2, 1, 2, 3, TimeSpan.Zero);
        var payroll = new Payroll
        {
            PeriodStart = new(2020, 10, 1),
            PeriodEnd = new(2020, 10, 31),
            StatusId = StatusIds.Payroll.SentBack,
            CreatedBySystem = true,
            SentAt = at,
            SentByAccountId = account.Id,
            SendBackNote = "Sửa lại ngày 15",
            SentBackAt = at,
            SentBackByAccountId = account.Id,
        };
        await ChangeAsync(api, db => db.Payrolls.Add(payroll));
        var before = await QueryAsync(api, db => db.Payrolls.AsNoTracking().SingleAsync());
        var audits = await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(Payroll)));

        await (await owner.PostRateAsync(jobType, 50_000, "2020-10-15")).BodyAsync(HttpStatusCode.OK);

        var after = await QueryAsync(api, db => db.Payrolls.AsNoTracking().SingleAsync());
        Assert.NotEqual(before.RowVersion, after.RowVersion);
        Assert.Equal(account.Id, after.UpdatedBy);
        Assert.Equal(
            (before.PeriodStart, before.PeriodEnd, before.StatusId, before.CreatedBySystem, before.SentAt, before.SentByAccountId, before.SendBackNote, before.SentBackAt, before.SentBackByAccountId, before.FinalizedAt, before.FinalizedByAccountId, before.CreatedAt, before.CreatedBy),
            (after.PeriodStart, after.PeriodEnd, after.StatusId, after.CreatedBySystem, after.SentAt, after.SentByAccountId, after.SendBackNote, after.SentBackAt, after.SentBackByAccountId, after.FinalizedAt, after.FinalizedByAccountId, after.CreatedAt, after.CreatedBy));
        Assert.Equal(audits, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(Payroll))));
    }

    [Fact]
    public async Task No_route_reads_or_writes_a_payroll_and_the_routes_of_this_pr_are_the_ones_of_the_client_list()
    {
        await using var factory = new AuthApiFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(endpoints, p => p.Contains("payrolls", StringComparison.OrdinalIgnoreCase) || p.Contains("pay-periods", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["api/payroll-settings", "api/payroll-settings", "api/payroll-settings/preview"],
            endpoints.Where(p => p.Contains("payroll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "api/job-types", "api/job-types", "api/job-types/options", "api/job-types/{id:guid}", "api/job-types/{id:guid}/discontinue",
                "api/job-types/{id:guid}/name", "api/job-types/{id:guid}/rates", "api/job-types/{id:guid}/resume",
            ],
            endpoints.Where(p => p.Contains("job-types", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/payrolls")).StatusCode);
    }
}
