using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-02 seasonal workers and teams: duplicates, membership, deactivation, access and personal data.</summary>
public class WorkforceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Workers = "/api/labor/workers";
    private const string Teams = "/api/labor/teams";

    [Fact]
    public async Task Worker_is_created_with_a_normalised_phone_linked_to_the_same_employee_and_audited()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync($"Lê Văn {NewCode()}");

        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(Workers, new
        {
            fullName = employee.FullName.ToUpperInvariant(),
            phoneNumber = "+84" + employee.PhoneNumber![1..],
        }), HttpStatusCode.Created);

        Assert.Equal(employee.PhoneNumber, created.GetProperty("phoneNumber").GetString());
        Assert.Equal(employee.Id, created.GetProperty("employee").GetProperty("id").GetGuid());
        Assert.Equal("In Use", created.GetProperty("status").GetString());
        Assert.Contains(await factory.AuditOfAsync(created.GetProperty("id").GetGuid()), e => e.Action == "SeasonalWorker.Created");
    }

    [Fact]
    public async Task Same_name_and_phone_or_same_citizen_id_is_MSG03_with_the_existing_id()
    {
        var w = await factory.WorldAsync();
        var phone = NewPhone();
        var citizenId = $"079{Random.Shared.Next(100_000_000, 999_999_999)}";
        var first = await ExpectAsync(await w.Owner.PostAsJsonAsync(Workers, new { fullName = "Trần Thị Mai", phoneNumber = phone, citizenId }), HttpStatusCode.Created);

        var sameNamePhone = await (await w.Owner.PostAsJsonAsync(Workers, new { fullName = "trần thị mai", phoneNumber = phone }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG03");
        var sameCitizen = await (await w.Owner.PostAsJsonAsync(Workers, new { fullName = "Người khác", citizenId }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG03");

        Assert.Equal("Seasonal worker already exists. The system opens the existing record for you to check and does not create a copy.",
            sameNamePhone.GetProperty("detail").GetString());
        Assert.Equal(first.GetProperty("id").GetGuid(), sameNamePhone.GetProperty("existingId").GetGuid());
        Assert.Equal(first.GetProperty("id").GetGuid(), sameCitizen.GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task Neither_phone_nor_citizen_id_is_MSG01()
    {
        var w = await factory.WorldAsync();

        var problem = await (await w.Owner.PostAsJsonAsync(Workers, new { fullName = "Không liên lạc" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");

        Assert.Equal("Phone number or citizen ID", problem.Field());
    }

    [Fact]
    public async Task Duplicate_team_name_ignoring_case_is_MSG03()
    {
        var w = await factory.WorldAsync();
        var name = $"Tổ Bè {NewCode()}";
        var team = await ExpectAsync(await w.Owner.PostAsJsonAsync(Teams, new { name }), HttpStatusCode.Created);

        var problem = await (await w.Owner.PostAsJsonAsync(Teams, new { name = name.ToUpperInvariant() })).ProblemAsync(HttpStatusCode.Conflict, "MSG03");

        Assert.StartsWith("Team already exists.", problem.GetProperty("detail").GetString());
        Assert.Equal(team.GetProperty("id").GetGuid(), problem.GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task Leader_must_be_a_member_and_the_team_shows_members_and_the_leader_phone()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var member = await w.AddWorkerAsync(team.Id);
        var outsider = await w.AddWorkerAsync();
        var detail = await ExpectAsync(await w.Owner.GetAsync($"{Teams}/{team.Id}"), HttpStatusCode.OK);

        var refused = await (await w.Owner.PutAsJsonAsync($"{Teams}/{team.Id}", new
        {
            name = team.Name, leaderWorkerId = outsider.Id, rowVersion = detail.GetProperty("rowVersion").GetString(),
        })).ProblemAsync(HttpStatusCode.BadRequest, "MSG01");
        var saved = await ExpectAsync(await w.Owner.PutAsJsonAsync($"{Teams}/{team.Id}", new
        {
            name = team.Name, leaderWorkerId = member.Id, rowVersion = detail.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);

        Assert.Equal("Leader", refused.Field());
        Assert.Equal(member.Id, saved.GetProperty("leader").GetProperty("id").GetGuid());
        Assert.Equal(member.PhoneNumber, saved.GetProperty("contactPhoneNumber").GetString());
        Assert.Equal(member.Id, Assert.Single(saved.GetProperty("members").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_leader_who_moves_to_another_team_is_no_longer_its_leader()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var other = await w.AddTeamAsync();
        var leader = await w.AddWorkerAsync(team.Id);
        await factory.UpdateAsync<Contractor>(team.Id, t => t.LeaderWorkerId = leader.Id);
        var worker = await ExpectAsync(await w.Owner.GetAsync($"{Workers}/{leader.Id}"), HttpStatusCode.OK);

        await ExpectAsync(await w.Owner.PutAsJsonAsync($"{Workers}/{leader.Id}", new
        {
            fullName = leader.FullName, phoneNumber = leader.PhoneNumber, teamId = other.Id, rowVersion = worker.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);

        var detail = await ExpectAsync(await w.Owner.GetAsync($"{Teams}/{team.Id}"), HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("leader").ValueKind);
        Assert.Empty(detail.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task Team_with_an_unpaid_payslip_cannot_be_deactivated_MSG11_lists_the_periods()
    {
        var w = await factory.WorldAsync();
        var team = await w.AddTeamAsync();
        var month = NewMonth();
        await factory.UseDbAsync(db =>
        {
            var payroll = new Payroll { Period = month.Key, PeriodStart = month.Start, PeriodEnd = month.End, PreparedByAccountId = w.OwnerAccount.Id };
            db.Payrolls.Add(payroll);
            db.LaborSettlements.Add(new LaborSettlement
            {
                PayrollId = payroll.Id, ContractorId = team.Id, PeriodStart = month.Start, PeriodEnd = month.End, TotalObligation = 93_000,
                PreparedByAccountId = w.OwnerAccount.Id, StatusId = StatusIds.LaborSettlement.Approved,
            });
        });

        var problem = await (await w.Owner.PostAsync($"{Teams}/{team.Id}/deactivate", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG11");

        Assert.Equal($"Cannot proceed because 1 records are still in use: payslips {month.Label}. Please handle these records first.",
            problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Workers_and_teams_are_deactivated_and_reactivated_and_the_same_status_is_MSG61()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();
        var team = await w.AddTeamAsync();

        var off = await ExpectAsync(await w.Owner.PostAsync($"{Workers}/{worker.Id}/deactivate", null), HttpStatusCode.OK);
        var again = await w.Owner.PostAsync($"{Workers}/{worker.Id}/deactivate", null);
        var on = await ExpectAsync(await w.Owner.PostAsync($"{Workers}/{worker.Id}/reactivate", null), HttpStatusCode.OK);
        var teamOff = await ExpectAsync(await w.Owner.PostAsync($"{Teams}/{team.Id}/deactivate", null), HttpStatusCode.OK);

        Assert.Equal("Discontinued", off.GetProperty("status").GetString());
        await again.ProblemAsync(HttpStatusCode.Conflict, "MSG61");
        Assert.Equal("In Use", on.GetProperty("status").GetString());
        Assert.Equal("Discontinued", teamOff.GetProperty("status").GetString());
        Assert.Equal(2, (await factory.AuditOfAsync(worker.Id)).Count(e => e.Action == "SeasonalWorker.StatusChanged"));
    }

    [Fact]
    public async Task Stale_row_version_is_MSG06()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync();

        var response = await w.Owner.PutAsJsonAsync($"{Workers}/{worker.Id}", new
        {
            fullName = worker.FullName, phoneNumber = worker.PhoneNumber, rowVersion = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
        });

        await response.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
    }

    [Fact]
    public async Task Zone_managers_with_labor_workers_in_any_zone_manage_workers_others_get_MSG02()
    {
        var w = await factory.WorldAsync();
        var (manager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.LaborWorkers, true, true, true, false));
        var (stranger, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.Attendance, true, true, true, false));
        var (accountant, _) = await factory.StaffAsync(null, (PermissionFunctions.Payroll, true, true, true, false));

        await ExpectAsync(await manager.PostAsJsonAsync(Workers, new { fullName = "Võ Văn Tám", phoneNumber = NewPhone() }), HttpStatusCode.Created);
        await (await stranger.GetAsync(Workers)).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await (await stranger.PostAsJsonAsync(Teams, new { name = $"Tổ {NewCode()}" })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
        await ExpectAsync(await accountant.GetAsync(Workers), HttpStatusCode.OK);
        await (await accountant.PostAsJsonAsync(Workers, new { fullName = "Kế toán tạo", phoneNumber = NewPhone() })).ProblemAsync(HttpStatusCode.Forbidden, "MSG02");
    }

    [Fact]
    public async Task Pickers_return_no_phone_or_citizen_id()
    {
        var w = await factory.WorldAsync();
        var worker = await w.AddWorkerAsync(configure: x => x.CitizenId = "012345678");
        var (manager, _) = await factory.StaffAsync(w.Zone, (PermissionFunctions.WorkAssignments, true, false, false, false));

        var response = await manager.GetAsync($"{w.Assignments}/assignees?search={Uri.EscapeDataString(worker.FullName)}");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(worker.Id.ToString(), text);
        Assert.DoesNotContain(worker.PhoneNumber!, text);
        Assert.DoesNotContain("012345678", text);
        Assert.DoesNotContain("phoneNumber", text);
    }
}
