using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// Decision of 12/11/2026, the crew of a harvest lot: an optional list of employees, saved with the lot, validated (they exist, still work in the cooperative), returned with the lot and kept in its
/// audit log. One employee may be on the lots of two rafts the same day, and nothing is counted per person.
/// </summary>
public class HarvestCrewTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<Employee> EmployeeAsync(string name, int statusId = StatusIds.Employee.Working, bool otherCooperative = false)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var employee = new Employee { CooperativeId = cooperative.Id, FullName = name, Code = $"NV{NewCode()}", StatusId = statusId };
        if (otherCooperative)
        {
            var stranger = new Cooperative { FullName = $"Another {NewCode()}" };
            await factory.UseDbAsync(db => db.Cooperatives.Add(stranger));
            employee.CooperativeId = stranger.Id;
        }

        await factory.UseDbAsync(db => db.Employees.Add(employee));
        return employee;
    }

    [Fact]
    public async Task A_lot_keeps_the_crew_it_is_recorded_with_and_returns_id_code_and_name_in_every_read()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var an = await EmployeeAsync("An");

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Crew = [hoa.Id.ToString(), an.Id.ToString()] });

        Assert.Equal(["An", "Hoa"], lot.GetProperty("crew").EnumerateArray().Select(c => c.Text("name")).ToList());
        Assert.Equal((hoa.Id, hoa.Code), (lot.GetProperty("crew")[1].Id(), lot.GetProperty("crew")[1].Text("code")));
        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal(2, read.GetProperty("crew").GetArrayLength());
        var listed = (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("lots").EnumerateArray().Single();
        Assert.Equal(["An", "Hoa"], listed.GetProperty("crew").EnumerateArray().Select(c => c.Text("name")).ToList());
        Assert.Equal(2, await factory.QueryAsync(db => db.HarvestLotCrew.CountAsync(c => c.LotId == lot.Id())));
    }

    [Fact]
    public async Task The_crew_is_optional_and_a_lot_without_one_reads_as_before()
    {
        var raft = await factory.SeededRaftAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);

        Assert.Equal(0, lot.GetProperty("crew").GetArrayLength());
        var audit = await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Single(a => a.Action == "HarvestLot.Created" && a.RecordId == lot.Id()).After!));
        Assert.DoesNotContain("Crew", audit);
    }

    [Fact]
    public async Task The_audit_log_names_the_crew_of_a_new_lot_and_of_a_correction()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var an = await EmployeeAsync("An");
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Crew = [hoa.Id.ToString()] });

        await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Crew = [hoa.Id.ToString(), an.Id.ToString()] });

        var events = await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Where(a => a.RecordId == lot.Id()).OrderBy(a => a.OccurredAt).ToList()));
        Assert.Contains("Hoa", events.Single(e => e.Action == "HarvestLot.Created").After);
        var corrected = events.Single(e => e.Action == "HarvestLot.Corrected");
        Assert.Contains("An", corrected.After);
    }

    [Fact]
    public async Task A_correction_without_the_field_keeps_the_crew_one_with_new_ids_replaces_it_and_an_empty_value_clears_it()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var an = await EmployeeAsync("An");
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Crew = [hoa.Id.ToString()] });

        var kept = await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "9" });
        Assert.Equal(["Hoa"], kept.GetProperty("lot").GetProperty("crew").EnumerateArray().Select(c => c.Text("name")).ToList());

        var replaced = await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "9", Crew = [an.Id.ToString()] });
        Assert.Equal(["An"], replaced.GetProperty("lot").GetProperty("crew").EnumerateArray().Select(c => c.Text("name")).ToList());

        var cleared = await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "9", ReplaceCrew = true });
        Assert.Equal(0, cleared.GetProperty("lot").GetProperty("crew").GetArrayLength());
        Assert.Equal(0, await factory.QueryAsync(db => db.HarvestLotCrew.CountAsync(c => c.LotId == lot.Id())));
    }

    [Fact]
    public async Task An_employee_may_be_on_the_lots_of_two_rafts_the_same_day_and_an_id_sent_twice_counts_once()
    {
        var first = await factory.SeededRaftAsync();
        var second = await factory.SeededRaftAsync();
        var hoa = await EmployeeAsync("Hoa");

        var lotA = await first.Owner.Client.HarvestLotAsync(first.RaftId, new HarvestFields { Crew = [hoa.Id.ToString(), hoa.Id.ToString()] });
        var lotB = await second.Owner.Client.HarvestLotAsync(second.RaftId, new HarvestFields { Crew = [hoa.Id.ToString()] });

        Assert.Equal(1, lotA.GetProperty("crew").GetArrayLength());
        Assert.Equal(1, lotB.GetProperty("crew").GetArrayLength());
        Assert.Equal(2, await factory.QueryAsync(db => db.HarvestLotCrew.CountAsync(c => c.EmployeeId == hoa.Id)));
    }

    [Fact]
    public async Task An_unknown_employee_one_who_left_one_of_another_cooperative_or_an_unreadable_id_is_refused_and_nothing_is_saved()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var left = await EmployeeAsync("Left", StatusIds.Employee.Left);
        var stranger = await EmployeeAsync("Stranger", otherCooperative: true);

        foreach (var crew in new[] { Guid.NewGuid().ToString(), left.Id.ToString(), stranger.Id.ToString(), "not-a-guid" })
        {
            var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Crew = [crew] })).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "Crew"), (refused.Text("code"), refused.Text("field")));
        }

        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task A_correction_with_a_bad_employee_changes_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Crew = [hoa.Id.ToString()] });

        var refused = await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Cages = "9", Crew = [Guid.NewGuid().ToString()] });

        Assert.Equal("Crew", (await refused.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal(8m, (await owner.GetHarvestLotAsync(lot.Id())).Kg("cageCount"));
    }

    [Fact]
    public async Task Removing_a_lot_removes_its_crew_rows_and_the_employee_stays()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Crew = [hoa.Id.ToString()] });

        await factory.RemoveLotAsync(owner, lot.Id());

        Assert.Equal(0, await factory.QueryAsync(db => db.HarvestLotCrew.CountAsync(c => c.LotId == lot.Id())));
        Assert.Equal(1, await factory.QueryAsync(db => db.Employees.CountAsync(e => e.Id == hoa.Id)));
    }

    [Fact]
    public async Task The_suggestions_are_the_employees_assigned_to_the_raft_that_day_who_still_work_here()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var hoa = await EmployeeAsync("Hoa");
        var an = await EmployeeAsync("An");
        var left = await EmployeeAsync("Left", StatusIds.Employee.Left);
        var elsewhere = await EmployeeAsync("Elsewhere");
        var otherRaft = (await owner.CreateRaftAsync(raft.ZoneId)).Id();
        var recorder = raft.Owner.Account.Id;
        var job = new JobType { Name = $"Harvest {NewCode()}" };
        await factory.UseDbAsync(db =>
        {
            db.JobTypes.Add(job);
            foreach (var (employee, raftId) in new[] { (hoa, raft.RaftId), (an, raft.RaftId), (left, raft.RaftId), (elsewhere, otherRaft) })
            {
                db.WorkAssignments.Add(new WorkAssignment { EmployeeId = employee.Id, RaftId = raftId, JobTypeId = job.Id, Date = Today, AssignedByAccountId = recorder });
            }
        });

        var suggestions = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/crew-suggestions");
        var tomorrow = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/crew-suggestions?date={Today.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(["An", "Hoa"], suggestions.GetProperty("employees").EnumerateArray().Select(e => e.Text("name")).ToList());
        Assert.Equal(raft.RaftId, suggestions.GetProperty("raftId").GetGuid());
        Assert.Empty(tomorrow.GetProperty("employees").EnumerateArray());
    }

    [Fact]
    public async Task The_suggestions_are_for_those_who_may_view_harvests_of_the_zone_and_an_unknown_raft_is_not_found()
    {
        var raft = await factory.SeededRaftAsync();
        var other = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var inZone = await factory.ManagerAsync(raft.ZoneId);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.GetAsync($"/api/rafts/{raft.RaftId}/crew-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inZone.Client.GetAsync($"/api/rafts/{raft.RaftId}/crew-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await raft.Owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/crew-suggestions")).StatusCode);
    }
}
