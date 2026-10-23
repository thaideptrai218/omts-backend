using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-02 rules not covered elsewhere: search and filters, duplicates through a "+84" phone, the automatic employee link
/// only for exactly one Employed match, the team of past attendance, deactivation of teams with draft or paid payslips,
/// and Discontinued teams kept in history but refused for new work.
/// </summary>
public class LaborWorkforceRuleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Workers = "/api/labor/workers";
    private const string Teams = "/api/labor/teams";

    private static List<Guid> Ids(JsonElement paged) => paged.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static string SheetOf(LaborWorld w, DateOnly date) => $"{w.Attendance}/{date:yyyy-MM-dd}";

    [Fact]
    public async Task Workers_are_found_by_name_phone_or_citizen_id_and_filtered_by_team_and_status()
    {
        var w = await factory.WorldAsync();
        var tag = NewCode();
        var team = await w.AddTeamAsync();
        var inTeam = await w.AddWorkerAsync(team.Id, x => x.FullName = $"Tìm {tag} Một");
        var byCitizenId = await w.AddWorkerAsync(configure: x => (x.FullName, x.PhoneNumber, x.CitizenId) = ($"Tìm {tag} Hai", null, $"07{Random.Shared.Next(1_000_000, 9_999_999)}"));
        var discontinued = await w.AddWorkerAsync(configure: x => (x.FullName, x.StatusId) = ($"Tìm {tag} Ba", StatusIds.Usage.Discontinued));

        var byName = await ExpectAsync(await w.Owner.GetAsync($"{Workers}?search={tag}"), HttpStatusCode.OK);
        var byPhone = await ExpectAsync(await w.Owner.GetAsync($"{Workers}?search={inTeam.PhoneNumber}"), HttpStatusCode.OK);
        var byCitizen = await ExpectAsync(await w.Owner.GetAsync($"{Workers}?search={byCitizenId.CitizenId}"), HttpStatusCode.OK);
        var ofTeam = await ExpectAsync(await w.Owner.GetAsync($"{Workers}?teamId={team.Id}"), HttpStatusCode.OK);
        var stopped = await ExpectAsync(await w.Owner.GetAsync($"{Workers}?search={tag}&statusId={StatusIds.Usage.Discontinued}"), HttpStatusCode.OK);
        var teamDetail = await ExpectAsync(await w.Owner.GetAsync($"{Teams}/{team.Id}"), HttpStatusCode.OK);

        Assert.Equal(new[] { inTeam.Id, byCitizenId.Id, discontinued.Id }.Order(), Ids(byName).Order());
        Assert.Equal(3, byName.GetProperty("total").GetInt32());
        Assert.Equal([inTeam.Id], Ids(byPhone));
        Assert.Equal([byCitizenId.Id], Ids(byCitizen));
        Assert.Equal([inTeam.Id], Ids(ofTeam));
        Assert.Equal([discontinued.Id], Ids(stopped));
        Assert.Equal([inTeam.Id], teamDetail.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Same_name_with_a_plus84_phone_is_MSG03_and_only_exactly_one_employed_match_is_linked()
    {
        var w = await factory.WorldAsync();
        var tag = NewCode();
        var phone = NewPhone();
        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(Workers, new { fullName = $"Trần Thị Hoa {tag}", phoneNumber = phone }), HttpStatusCode.Created);
        var twinPhone = NewPhone();
        foreach (var _ in new[] { 1, 2 })
        {
            await w.AddEmployeeAsync($"Lý Văn Đôi {tag}", e => e.PhoneNumber = twinPhone);
        }

        var onePhone = NewPhone();
        var employed = await w.AddEmployeeAsync($"Mai Văn Một {tag}", e => e.PhoneNumber = onePhone);
        await w.AddEmployeeAsync($"Mai Văn Một {tag}", e => (e.PhoneNumber, e.StatusId, e.EmploymentEndDate) = (onePhone, StatusIds.Employee.Left, new DateOnly(2026, 1, 31)));

        var duplicate = await (await w.Owner.PostAsJsonAsync(Workers, new { fullName = $"trần thị hoa {tag}", phoneNumber = "+84" + phone[1..] }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG03");
        var ambiguous = await ExpectAsync(await w.Owner.PostAsJsonAsync(Workers, new { fullName = $"Lý Văn Đôi {tag}", phoneNumber = twinPhone }), HttpStatusCode.Created);
        var linked = await ExpectAsync(await w.Owner.PostAsJsonAsync(Workers, new { fullName = $"Mai Văn Một {tag}", phoneNumber = onePhone }), HttpStatusCode.Created);

        Assert.Equal(created.GetProperty("id").GetGuid(), duplicate.GetProperty("existingId").GetGuid());
        Assert.StartsWith("Seasonal worker already exists.", duplicate.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, ambiguous.GetProperty("employee").ValueKind);
        Assert.Equal(employed.Id, linked.GetProperty("employee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Changing_a_worker_s_team_keeps_the_team_of_past_attendance_and_applies_to_later_days()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var (before, after) = (await w.AddTeamAsync(), await w.AddTeamAsync());
        var worker = await w.AddWorkerAsync(before.Id);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(5)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var current = await ExpectAsync(await w.Owner.GetAsync($"{Workers}/{worker.Id}"), HttpStatusCode.OK);

        await ExpectAsync(await w.Owner.PutAsJsonAsync($"{Workers}/{worker.Id}", new
        {
            fullName = worker.FullName, phoneNumber = worker.PhoneNumber, teamId = after.Id, rowVersion = current.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(6)), new { rows = new[] { new { workerId = worker.Id, result = "Present" } } }), HttpStatusCode.OK);
        var past = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={month.Day(5):yyyy-MM-dd}"), HttpStatusCode.OK);
        var later = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={month.Day(6):yyyy-MM-dd}"), HttpStatusCode.OK);

        Guid TeamOf(JsonElement sheet) => sheet.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("personId").GetGuid() == worker.Id).GetProperty("team").GetProperty("id").GetGuid();
        Assert.Equal((before.Id, after.Id), (TeamOf(past), TeamOf(later)));
    }

    [Fact]
    public async Task Team_with_a_draft_payslip_cannot_be_deactivated_and_a_fully_paid_one_can()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var team = await w.AddTeamAsync();
        await w.AddPieceworkAsync(month.Day(8), null, null, team.Id, completed: 10);
        var payslip = (await w.CreatePayrollAsync(month)).PayslipOf(team.Id).GetProperty("id").GetGuid();

        var draft = await (await w.Owner.PostAsync($"{Teams}/{team.Id}/deactivate", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG11");
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsJsonAsync($"/api/payslips/{payslip}/payments", new { amountPaid = 25_000, paymentDate = month.End, method = "Cash" }), HttpStatusCode.Created);
        var paid = await ExpectAsync(await w.Owner.PostAsync($"{Teams}/{team.Id}/deactivate", null), HttpStatusCode.OK);

        Assert.Equal($"Cannot proceed because 1 records are still in use: payslips {month.Label}. Please handle these records first.", draft.GetProperty("detail").GetString());
        Assert.Equal("Discontinued", paid.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Discontinued_team_is_refused_for_new_work_and_attendance_but_its_history_stays_and_it_can_come_back()
    {
        var w = await factory.WorldAsync();
        var month = NewMonth();
        var team = await w.AddTeamAsync();
        var member = await w.AddWorkerAsync(team.Id);
        object Daily(int day) => new { workDate = month.Day(day), teamId = team.Id, task = "Kéo bè", paymentType = "DailyWage" };
        var old = await ExpectAsync(await w.Owner.PostAsJsonAsync(w.Assignments, Daily(3)), HttpStatusCode.Created);
        await ExpectAsync(await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(3)), new { rows = new[] { new { teamId = team.Id, result = "Present" } } }), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsync($"{Teams}/{team.Id}/deactivate", null), HttpStatusCode.OK);

        var work = await (await w.Owner.PostAsJsonAsync(w.Assignments, Daily(4))).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var sheet = await (await w.Owner.PutAsJsonAsync(SheetOf(w, month.Day(4)), new { rows = new[] { new { teamId = team.Id, result = "Present" } } }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var history = await ExpectAsync(await w.Owner.GetAsync($"{w.Assignments}/{old.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        var pastSheet = await ExpectAsync(await w.Owner.GetAsync($"{w.Attendance}?date={month.Day(3):yyyy-MM-dd}"), HttpStatusCode.OK);
        await ExpectAsync(await w.Owner.PostAsync($"{Teams}/{team.Id}/reactivate", null), HttpStatusCode.OK);
        var back = await w.Owner.PostAsJsonAsync(w.Assignments, Daily(4));

        Assert.Equal(("Team", "Team"), (work.Field(), sheet.Field()));
        Assert.Equal((team.Id, "Active"), (history.GetProperty("assignee").GetProperty("id").GetGuid(), history.GetProperty("state").GetString()));
        Assert.Contains(pastSheet.GetProperty("rows").EnumerateArray(), r => r.GetProperty("personId").GetGuid() == member.Id && r.GetProperty("team").GetProperty("id").GetGuid() == team.Id);
        Assert.Equal(HttpStatusCode.Created, back.StatusCode);
    }
}
