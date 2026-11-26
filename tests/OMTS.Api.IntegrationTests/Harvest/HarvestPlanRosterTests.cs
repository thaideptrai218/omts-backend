using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Harvest.HarvestPlanTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// Owner decisions of 26/11/2026: the Owner chooses the employees (staff and seasonal workers) who harvest a plan; each answers in the mobile app; the plan shows how many confirmed against the people needed; the Owner can confirm
/// for an employee with a reason, and add people hired outside the system (they count at once, with a seasonal worker record and its login). Warns, never blocks.
/// </summary>
public class HarvestPlanRosterTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string Roster(Guid planId) => $"{PlanRoute}/{planId}/staffing";

    private sealed record Person(Guid EmployeeId, Guid AccountId, HttpClient Client);

    /// <summary>A staff employee of the zone with a login account of their own, as the mobile app signs one in.</summary>
    private async Task<Person> PersonAsync(HttpClient owner, Guid zoneId)
    {
        var employeeId = IdOf(await CreateAsync(owner, StaffProfile($"Emp {NewCode()}", NewPhone(), zoneId)));
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(account, StaffRole.Employee, null);
        await factory.UpdateAsync<Employee>(employeeId, e => e.AccountId = account.Id);
        return new Person(employeeId, account.Id, await factory.LoginAsync(account.Username));
    }

    private async Task<JsonElement> MineAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/harvest-shifts");
        Assert.True(response.IsSuccessStatusCode, string.Join(" | ", factory.Logs.Lines.Where(l => l.StartsWith("Error") || l.StartsWith("Critical")).TakeLast(3)));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> RosterOfAsync(HttpClient client, Guid planId) => await (await client.GetAsync($"{Roster(planId)}/roster")).ReadAsync(HttpStatusCode.OK);

    private static JsonElement RowOf(JsonElement roster, Guid employeeId) => roster.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("employeeId").GetGuid() == employeeId);

    private static int Count(JsonElement roster, string name) => roster.GetProperty("summary").GetProperty(name).GetInt32();

    private static async Task<JsonElement> InviteAsync(HttpClient client, Guid planId, params Guid[] employeeIds) =>
        await (await client.PostAsJsonAsync($"{Roster(planId)}/invite", new { employeeIds })).ReadAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> AnswerAsync(HttpClient client, Guid rowId, bool accept, string? reason = null) =>
        client.PostAsJsonAsync($"/api/me/harvest-shifts/{rowId}/response", new { accept, reason });

    private async Task<(SeededRaft Raft, JsonElement Plan)> AssignedPlanAsync(string? executor = null)
    {
        var raft = await factory.SeededRaftAsync();
        var plan = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));
        var assigned = await raft.Owner.Client.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/assign", executor is null ? (object)new { } : new { executor });
        return (raft, (await assigned.ReadAsync(HttpStatusCode.OK)).GetProperty("plan"));
    }

    [Fact]
    public async Task The_owner_invites_employees_who_accept_or_decline_and_the_plan_counts_them_against_the_people_needed()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;
        var (first, second) = (await PersonAsync(owner, raft.ZoneId), await PersonAsync(owner, raft.ZoneId));
        (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 2 })).EnsureSuccessStatusCode();

        var invited = await InviteAsync(owner, plan.Id(), first.EmployeeId, second.EmployeeId);

        Assert.Equal("Invited", RowOf(invited, first.EmployeeId).Text("state"));
        Assert.Equal((2, 0, 0, 2, false), (Count(invited, "invited"), Count(invited, "confirmed"), Count(invited, "declined"), Count(invited, "missing"), invited.GetProperty("summary").GetProperty("ready").GetBoolean()));
        Assert.Equal(2, invited.GetProperty("requiredPeople").GetInt32());
        // Each employee is told through the existing notification mechanism, and sees the invitation in the app without the customer or the cages.
        var notices = await factory.NoticesAsync(NotificationTypes.HarvestShiftInvited, plan.Id());
        Assert.Equal(new Guid?[] { first.AccountId, second.AccountId }.Order().ToList(), notices.Select(n => n.RecipientAccountId).Order().ToList());
        var mine = await MineAsync(first.Client);
        var shift = Assert.Single(mine.EnumerateArray());
        Assert.Equal((plan.Text("code"), "Invited", true), (shift.Text("planCode"), shift.Text("state"), shift.GetProperty("canRespond").GetBoolean()));
        Assert.False(shift.TryGetProperty("orders", out _));

        var accepted = await (await AnswerAsync(first.Client, shift.GetProperty("rowId").GetGuid(), true)).ReadAsync(HttpStatusCode.OK);
        var declinedRow = (await MineAsync(second.Client))[0].GetProperty("rowId").GetGuid();
        var declined = await (await AnswerAsync(second.Client, declinedRow, false, "Bận việc nhà")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Confirmed", "Declined"), (accepted.Text("state"), declined.Text("state")));
        var roster = await RosterOfAsync(owner, plan.Id());
        Assert.Equal(("Confirmed", "Declined", "Bận việc nhà"), (RowOf(roster, first.EmployeeId).Text("state"), RowOf(roster, second.EmployeeId).Text("state"), RowOf(roster, second.EmployeeId).Text("reason")));
        Assert.Equal((1, 0, 1, 1, false), (Count(roster, "confirmed"), Count(roster, "invited"), Count(roster, "declined"), Count(roster, "missing"), roster.GetProperty("summary").GetProperty("ready").GetBoolean()));
        // The Owner is told of each answer, and the plan view carries the same numbers for its list.
        Assert.True((await factory.NoticesAsync(NotificationTypes.HarvestShiftResponded, plan.Id())).Count >= 2);
        var view = await owner.GetPlanAsync(plan.Id());
        Assert.Equal((1, 2), (view.GetProperty("staffing").GetProperty("confirmed").GetInt32(), view.GetProperty("staffing").GetProperty("requiredPeople").GetInt32()));
        // Answering again with the same answer changes nothing; the employee may change their mind while the plan is open.
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(first.Client, shift.GetProperty("rowId").GetGuid(), true)).StatusCode);
        var changed = await (await AnswerAsync(second.Client, declinedRow, true)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Confirmed", changed.Text("state"));
        Assert.True((await RosterOfAsync(owner, plan.Id())).GetProperty("summary").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Confirming_for_an_employee_needs_a_reason_and_is_audited_with_who_and_when()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;
        var person = await PersonAsync(owner, raft.ZoneId);
        var rowId = RowOf(await InviteAsync(owner, plan.Id(), person.EmployeeId), person.EmployeeId).Id();

        var without = await owner.PostAsJsonAsync($"{Roster(plan.Id())}/{rowId}/confirm", new { reason = "  " });
        var confirmed = await (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/{rowId}/confirm", new { reason = "Đã báo qua điện thoại" })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Reason", await without.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var row = RowOf(confirmed, person.EmployeeId);
        Assert.Equal(("ConfirmedManually", "Đã báo qua điện thoại"), (row.Text("state"), row.Text("reason")));
        Assert.Equal(raft.Owner.Account.DisplayName, row.Text("confirmedBy"));
        Assert.Equal(1, Count(confirmed, "confirmed"));
        var audited = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(a => a.Action == "HarvestPlan.StaffConfirmedManually" && a.RecordId == plan.Id()).ToListAsync());
        var entry = Assert.Single(audited);
        Assert.Equal("Đã báo qua điện thoại", entry.Reason);
    }

    [Fact]
    public async Task Adding_a_person_hired_outside_the_system_makes_a_seasonal_worker_with_a_login_and_counts_at_once()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;
        (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 1 })).EnsureSuccessStatusCode();
        var phone = NewPhone();

        var noPhone = await owner.PostAsJsonAsync($"{Roster(plan.Id())}/external", new { fullName = "Người thuê ngoài" });
        var added = await (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/external", new { fullName = "Người thuê ngoài", phoneNumber = phone, note = "Hàng xóm", address = "Ấp 3" })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal("Phone number", await noPhone.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var person = added.GetProperty("person");
        Assert.Equal(("External", "SeasonalWorker", true), (person.Text("state"), person.Text("type"), person.GetProperty("hasAccount").GetBoolean()));
        Assert.Equal("OMTS@1234", added.Text("temporaryPassword"));
        Assert.True(added.GetProperty("fixedPassword").GetBoolean());
        Assert.False(string.IsNullOrEmpty(added.Text("username")));
        var summary = added.GetProperty("roster").GetProperty("summary");
        Assert.Equal((0, 1, true), (summary.GetProperty("confirmed").GetInt32(), summary.GetProperty("external").GetInt32(), summary.GetProperty("ready").GetBoolean()));
        var employee = await factory.QueryAsync(db => db.Employees.AsNoTracking().Include(e => e.TypePeriods).SingleAsync(e => e.Id == person.GetProperty("employeeId").GetGuid()));
        Assert.Equal((phone, "Hàng xóm", "Ấp 3", true), (employee.PhoneNumber, employee.Notes, employee.Address, employee.AccountId is not null));
        Assert.Equal(EmployeeType.SeasonalWorker, employee.TypePeriods.Single().Type);
        // A phone number another working employee has is refused (the answer names them: invite that employee instead).
        var again = await owner.PostAsJsonAsync($"{Roster(plan.Id())}/external", new { fullName = "Trùng số", phoneNumber = phone });
        await again.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03");
    }

    [Fact]
    public async Task The_number_needed_is_editable_until_the_plan_is_over_and_never_blocks_the_harvest()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;

        var zero = await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 0 });
        var tooMany = await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 501 });
        var set = await (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 5 })).ReadAsync(HttpStatusCode.OK);
        var changed = await (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 3 })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Required people", await zero.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Required people", await tooMany.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal((5, false, 5), (set.GetProperty("requiredPeople").GetInt32(), set.GetProperty("summary").GetProperty("ready").GetBoolean(), Count(set, "missing")));
        Assert.Equal(3, changed.GetProperty("requiredPeople").GetInt32());
        // Short of people, the harvest still starts: the owner's rule is to warn, not to block.
        var received = await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/receive", new { });
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        // Once the plan is cancelled the roster is closed (MSG-GEN-09) and the employee side too.
        var person = await PersonAsync(owner, raft.ZoneId);
        var rowId = RowOf(await InviteAsync(owner, plan.Id(), person.EmployeeId), person.EmployeeId).Id();
        (await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/cancel", new { reason = "Hủy thử" })).EnsureSuccessStatusCode();
        await (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/required", new { requiredPeople = 2 })).PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/invite", new { employeeIds = new[] { person.EmployeeId } })).PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await AnswerAsync(person.Client, rowId, true)).PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task The_zone_manager_staffs_only_a_plan_the_zone_managers_harvest_in_its_zones_and_an_owner_plan_is_not_there_for_it()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var ownerPlan = await raft.Owner.Client.OwnerExecutedPlanAsync(PlanBody(PlanLine(raft.RaftId, [2])));
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var other = await factory.ManagerAsync((await factory.AddZoneAsync(NewCode())).Id);
        var person = await PersonAsync(raft.Owner.Client, raft.ZoneId);

        var invited = await InviteAsync(manager.Client, plan.Id(), person.EmployeeId);

        Assert.True(invited.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Invited", RowOf(invited, person.EmployeeId).Text("state"));
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.GetAsync($"{Roster(plan.Id())}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Client.GetAsync($"{Roster(ownerPlan.Id())}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Client.PostAsJsonAsync($"{Roster(ownerPlan.Id())}/invite", new { employeeIds = new[] { person.EmployeeId } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"{Roster(plan.Id())}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync($"{Roster(plan.Id())}/invite", new { employeeIds = new[] { person.EmployeeId } })).StatusCode);
        // The Owner invites for his own plan; the staffing answer of the plan carries the roster beside the crew of the lines.
        var ownerInvited = await InviteAsync(raft.Owner.Client, ownerPlan.Id(), person.EmployeeId);
        Assert.Equal(1, Count(ownerInvited, "invited"));
        var staffing = await raft.Owner.Client.GetFromJsonAsync<JsonElement>(Roster(plan.Id()));
        Assert.Equal(1, staffing.GetProperty("roster").GetProperty("summary").GetProperty("invited").GetInt32());
    }

    [Fact]
    public async Task An_employee_answers_only_their_own_invitation_and_the_roster_poll_answers_304_while_nothing_changed()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;
        var (mine, other) = (await PersonAsync(owner, raft.ZoneId), await PersonAsync(owner, raft.ZoneId));
        var roster = await InviteAsync(owner, plan.Id(), mine.EmployeeId);
        var rowId = RowOf(roster, mine.EmployeeId).Id();

        Assert.Equal(HttpStatusCode.Forbidden, (await AnswerAsync(other.Client, rowId, true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mine.Client.PostAsJsonAsync($"/api/me/harvest-shifts/{rowId}/response", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AnswerAsync(mine.Client, Guid.NewGuid(), true)).StatusCode);
        Assert.Empty((await MineAsync(other.Client)).EnumerateArray());

        var first = await owner.GetAsync($"{Roster(plan.Id())}/roster");
        var etag = first.Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Roster(plan.Id())}/roster");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await owner.SendAsync(request)).StatusCode);
        (await AnswerAsync(mine.Client, rowId, true)).EnsureSuccessStatusCode();
        var next = new HttpRequestMessage(HttpMethod.Get, $"{Roster(plan.Id())}/roster");
        next.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var after = await owner.SendAsync(next);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(etag, after.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task The_picker_save_invites_the_new_people_and_takes_off_those_left_out_but_keeps_people_added_outside_the_system()
    {
        var (raft, plan) = await AssignedPlanAsync();
        var owner = raft.Owner.Client;
        var (a, b) = (await PersonAsync(owner, raft.ZoneId), await PersonAsync(owner, raft.ZoneId));
        await InviteAsync(owner, plan.Id(), a.EmployeeId);
        var external = await (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/external", new { fullName = "Ngoài", phoneNumber = NewPhone() })).ReadAsync(HttpStatusCode.Created);

        var saved = await (await owner.PutAsJsonAsync($"{Roster(plan.Id())}/roster", new { employeeIds = new[] { b.EmployeeId }, requiredPeople = 4 })).ReadAsync(HttpStatusCode.OK);

        var states = saved.GetProperty("people").EnumerateArray().Select(p => p.Text("state")).Order().ToList();
        Assert.Equal(["External", "Invited"], states);
        Assert.Equal(b.EmployeeId, saved.GetProperty("people").EnumerateArray().Single(p => p.Text("state") == "Invited").GetProperty("employeeId").GetGuid());
        Assert.Equal(4, saved.GetProperty("requiredPeople").GetInt32());
        var rowId = external.GetProperty("person").Id();
        var removed = await (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/{rowId}/remove", new { reason = "Không đến" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1, removed.GetProperty("people").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"{Roster(plan.Id())}/invite", new { employeeIds = new[] { Guid.NewGuid() } })).StatusCode);
    }
}
