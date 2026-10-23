using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-03 work assignment: assignee rules, MSG49 confirmation, cancel, period lock, notifications and zone access.</summary>
public class WorkAssignmentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static object Daily(DateOnly date, Guid? employeeId = null, Guid? workerId = null, Guid? teamId = null, bool confirm = false, string task = "Vệ sinh lồng") =>
        new { workDate = date, employeeId, workerId, teamId, task, paymentType = "DailyWage", confirmDuplicate = confirm };

    [Fact]
    public async Task Piecework_for_a_worker_is_saved_and_audited()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(10);

        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, new
        {
            workDate = date, workerId = worker.Id, task = "Buộc dây", notes = "Bè 3", paymentType = "Piecework",
            pieceUnit = "rope", plannedQuantity = 40, pieceRate = 500, expectedCompletionDate = date.AddDays(2),
        }), HttpStatusCode.Created);

        Assert.Equal(("Worker", worker.Id), (created.GetProperty("assignee").GetProperty("kind").GetString(), created.GetProperty("assignee").GetProperty("id").GetGuid()));
        Assert.Equal(("Active", 500m), (created.GetProperty("state").GetString(), created.GetProperty("pieceRate").GetDecimal()));
        Assert.Contains(await factory.AuditOfAsync(created.GetProperty("id").GetGuid()), e => e.Action == "WorkAssignment.Created" && e.ZoneId == w.Zone.Id);
    }

    [Fact]
    public async Task Employee_who_left_on_or_before_the_work_date_is_MSG72()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var employee = await w.AddEmployeeAsync("Phạm Văn Lâm", e => (e.StatusId, e.EmploymentEndDate) = (StatusIds.Employee.Left, month.Day(15)));

        var onEndDate = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(month.Day(15), employee.Id))).ProblemAsync(HttpStatusCode.Conflict, "MSG72");
        var before = await w.Owner.PostAsJsonAsync(w.Assignments, Daily(month.Day(14), employee.Id));

        Assert.Equal($"Phạm Văn Lâm has left since 15/{month.Month:D2}/{month.Year}. Work cannot be assigned or attendance recorded from this date.",
            onEndDate.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Created, before.StatusCode);
    }

    [Fact]
    public async Task Discontinued_worker_or_team_raft_of_another_zone_and_closed_cycle_are_MSG01()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(5);
        var worker = await w.AddWorkerAsync(configure: x => x.StatusId = StatusIds.Usage.Discontinued);
        var team = await w.AddTeamAsync(t => t.StatusId = StatusIds.Usage.Discontinued);
        var active = await w.AddWorkerAsync();
        var otherZone = await factory.AddZoneAsync(NewCode());
        var foreignRaft = new Raft { ZoneId = otherZone.Id, Code = NewCode() };
        var raft = new Raft { ZoneId = w.Zone.Id, Code = NewCode() };
        var closed = new CultivationCycle { RaftId = raft.Id, OpenedAt = DateTimeOffset.UtcNow.AddDays(-90), StatusId = StatusIds.CultivationCycle.Closed };
        await factory.UseDbAsync(db => db.AddRange(foreignRaft, raft, closed));

        var fields = new List<string?>();
        foreach (var body in new object[]
        {
            Daily(date, workerId: worker.Id),
            Daily(date, teamId: team.Id),
            new { workDate = date, workerId = active.Id, task = "x", paymentType = "DailyWage", raftId = foreignRaft.Id },
            new { workDate = date, workerId = active.Id, task = "x", paymentType = "DailyWage", raftId = raft.Id, cycleId = closed.Id },
        })
        {
            fields.Add((await (await w.Owner.PostAsJsonAsync(w.Assignments, body)).ProblemAsync(HttpStatusCode.BadRequest, "MSG01")).Field());
        }

        Assert.Equal(["Worker", "Team", "Raft", "Cycle"], fields);
    }

    [Fact]
    public async Task Same_person_on_the_same_date_asks_for_confirmation_MSG49_and_confirming_saves()
    {
        var w = await factory.WorldAsync();
        var date = NewMonth().Day(7);
        var team = await w.AddTeamAsync();
        var member = await w.AddWorkerAsync(team.Id, x => x.FullName = $"Đỗ Văn {NewCode()}");
        await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, workerId: member.Id)), HttpStatusCode.Created);

        var warning = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, teamId: team.Id))).ProblemAsync(HttpStatusCode.Conflict, "MSG49");
        var confirmed = await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, teamId: team.Id, confirm: true));

        Assert.True(warning.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal($"{member.FullName} has already been assigned on {date:dd/MM/yyyy}. Do you want to continue saving?",
            warning.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
    }

    [Fact]
    public async Task Cancel_needs_a_reason_and_is_refused_after_completion_MSG61()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(3);
        var completed = await w.AddPieceworkAsync(date, null, worker.Id, null, completed: 10);
        var active = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date.AddDays(1), workerId: worker.Id)), HttpStatusCode.Created);
        var activeId = active.GetProperty("id").GetGuid();

        var noReason = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{activeId}/cancel", new { reason = " " })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var cancelled = await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{activeId}/cancel", new { reason = "Mưa lớn" }), HttpStatusCode.OK);
        var afterCompletion = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{completed.Id}/cancel", new { reason = "Nhầm" }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG61");

        Assert.Equal("Reason", noReason.Field());
        Assert.Equal(("Cancelled", "Mưa lớn"), (cancelled.GetProperty("state").GetString(), cancelled.GetProperty("cancellation").GetProperty("reason").GetString()));
        Assert.Equal("Cannot change Work assignment from “Completed” to “Cancelled”: completion has already been confirmed.",
            afterCompletion.GetProperty("detail").GetString());
        Assert.Contains(await factory.AuditOfAsync(activeId), e => e.Action == "WorkAssignment.Cancelled" && e.Reason == "Mưa lớn");
    }

    [Fact]
    public async Task Date_in_a_finalized_month_is_locked_for_create_edit_and_cancel()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        var existing = await w.AddPieceworkAsync(month.Day(20), null, worker.Id, null);
        var finalized = await w.FinalizeMonthAsync(month);

        var created = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(month.Day(21), workerId: worker.Id))).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var cancelled = await (await w.Owner.PostAsJsonAsync($"{w.Assignments}/{existing.Id}/cancel", new { reason = "x" })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var moved = await w.Owner.PutAsJsonAsync($"{w.Assignments}/{existing.Id}", new
        {
            workDate = month.Day(20), workerId = worker.Id, task = "Đổi", paymentType = "Piecework", pieceUnit = "rope", plannedQuantity = 40, pieceRate = 2500,
            rowVersion = Convert.ToBase64String(existing.RowVersion),
        });

        Assert.Equal($"The payroll of {finalized.Label} has been finalized. Work, attendance and pay of this period can no longer be changed.",
            created.GetProperty("detail").GetString());
        Assert.Equal(finalized.Key, cancelled.GetProperty("period").GetString());
        await moved.ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }

    [Fact]
    public async Task Employee_with_an_active_account_is_notified_of_new_rescheduled_and_cancelled_work()
    {
        var w = await factory.WorldAsync();
        var (employee, _, account) = await w.AddEmployeeWithAccountAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(12);

        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, employee.Id, task: "Kéo bè")), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await ExpectAsync(await w.Owner.PutAsJsonAsync($"{w.Assignments}/{id}", new
        {
            workDate = date.AddDays(1), employeeId = employee.Id, task = "Kéo bè", paymentType = "DailyWage", rowVersion = created.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"{w.Assignments}/{id}/cancel", new { reason = "Đổi kế hoạch" }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(date, workerId: worker.Id)), HttpStatusCode.Created);

        var notifications = await factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RecipientAccountId == account.Id && n.Type == NotificationTypes.WorkAssignment).OrderBy(n => n.CreatedAt).ToListAsync());
        Assert.Equal(["New work assignment", "New work assignment", "Work assignment cancelled"], notifications.Select(n => n.Title));
        Assert.Equal($"Kéo bè – {w.Zone.Name} – {date:dd/MM/yyyy}", notifications[0].Body);
        Assert.Equal($"Kéo bè – {w.Zone.Name} – {date.AddDays(1):dd/MM/yyyy}", notifications[1].Body);
        Assert.Equal(3, factory.Services.GetRequiredService<FakePushSender>().Inbox.List(account.Id.ToString()).Count);
    }

    [Fact]
    public async Task A_failing_push_is_logged_and_never_blocks_the_save()
    {
        var w = await factory.WorldAsync();
        var (employee, _, account) = await w.AddEmployeeWithAccountAsync();
        factory.Services.GetRequiredService<IOptions<PushOptions>>().Value.Fake.FailFor.Add(account.Id.ToString());

        var response = await w.Owner.PostAsJsonAsync(w.Assignments, Daily(NewMonth().Day(2), employee.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == account.Id)));
        Assert.Contains(factory.Logs.Lines, l => l.Contains("WORK_ASSIGNMENT") && l.Contains(account.Id.ToString()) && l.Contains("failed"));
    }

    [Fact]
    public async Task Zone_not_granted_is_MSG02_and_an_assignment_of_another_zone_is_not_found()
    {
        var w = await factory.WorldAsync();
        var otherZone = await factory.AddZoneAsync(NewCode());
        var worker = await w.AddWorkerAsync();
        var (manager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.WorkAssignments, true, true, true, true));
        var foreign = await w.AddPieceworkAsync(NewMonth().Day(1), null, worker.Id, null);
        await factory.UpdateAsync<OMTS.Domain.Entities.Labor.WorkAssignment>(foreign.Id, a => a.ZoneId = otherZone.Id);

        await (await manager.PostAsJsonAsync($"/api/zones/{otherZone.Id}/work-assignments", Daily(NewMonth().Day(1), workerId: worker.Id)))
            .ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await manager.GetAsync($"{w.Assignments}/{foreign.Id}")).ProblemAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await ExpectAsync(await manager.PostAsJsonAsync(w.Assignments, Daily(NewMonth().Day(1), workerId: worker.Id)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task After_completion_only_task_and_notes_can_change()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var date = NewMonth().Day(9);
        var done = await w.AddPieceworkAsync(date, null, worker.Id, null, completed: 12);
        var current = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}/{done.Id}"), HttpStatusCode.OK);
        object Body(decimal rate, string task) => new
        {
            workDate = date, workerId = worker.Id, task, paymentType = "Piecework", pieceUnit = "rope", plannedQuantity = 40, pieceRate = rate,
            rowVersion = current.GetProperty("rowVersion").GetString(),
        };

        var rateChange = await (await w.Owner.PutAsJsonAsync($"{w.Assignments}/{done.Id}", Body(3_000, "Vệ sinh dây"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var taskChange = await ExpectAsync(await w.Owner.PutAsJsonAsync($"{w.Assignments}/{done.Id}", Body(2_500, "Vệ sinh dây bè 2")), HttpStatusCode.OK);

        Assert.Equal("Piece rate", rateChange.Field());
        Assert.Equal(("Vệ sinh dây bè 2", "Completed"), (taskChange.GetProperty("task").GetString(), taskChange.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task List_filters_by_state_and_payment_type()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var month = NewMonth();
        await w.AddPieceworkAsync(month.Day(4), null, worker.Id, null, completed: 5);
        await w.AddPieceworkAsync(month.Day(5), null, worker.Id, null);

        var completed = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}?from={month.Start:yyyy-MM-dd}&to={month.End:yyyy-MM-dd}&state=completed"), HttpStatusCode.OK);
        var piecework = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}?from={month.Start:yyyy-MM-dd}&to={month.End:yyyy-MM-dd}&paymentType=Piecework"), HttpStatusCode.OK);
        var badState = await w.Owner.GetAsync($"{w.Assignments}?state=done");

        Assert.Equal(1, completed.GetProperty("total").GetInt32());
        Assert.Equal(2, piecework.GetProperty("total").GetInt32());
        await badState.ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
    }
}
