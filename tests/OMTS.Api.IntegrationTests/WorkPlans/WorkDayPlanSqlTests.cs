using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.WorkPlans;

public sealed class WorkDayPlanSqlTests
{
    [SqlServerFact]
    public async Task Migration_enforces_plan_response_and_employee_versions_serialize_competing_reservations()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var key = Guid.NewGuid().ToString("N");
        var cooperative = new Cooperative { FullName = "Work plan SQL verification" };
        var owner = new Account { Username = key + "o", DisplayName = "Owner", PasswordHash = Guid.NewGuid().ToString("N"), SubjectType = SubjectType.CooperativeOwner };
        var manager = new Account { Username = key + "m", DisplayName = "Manager", PasswordHash = Guid.NewGuid().ToString("N"), SubjectType = SubjectType.Staff };
        var employee = new Employee { Cooperative = cooperative, FullName = "Crew" };
        var zone = new FarmingZone { Code = key, Name = "SQL zone", Cooperative = cooperative };
        var raft = new Raft { Code = key, Zone = zone };
        var job = new JobType { Name = "Harvest SQL" };
        var plan = new WorkDayPlan { CreatorAccount = owner, ResponsibleAccount = manager, Raft = raft,
            JobType = job, WorkDate = new DateOnly(2030, 1, 2), ClientOperationId = Guid.NewGuid() };
        db.AddRange(plan, employee);
        await db.SaveChangesAsync();
        plan.Approved = false; plan.RespondedAt = DateTimeOffset.UtcNow; plan.ResponseReason = null;
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_WorkDayPlan_Response", failure.InnerException?.Message);
        db.ChangeTracker.Clear();
        var saved = await db.Set<WorkDayPlan>().SingleAsync(p => p.Id == plan.Id);
        Assert.Null(saved.Approved);
        saved.Approved = false; saved.RespondedAt = DateTimeOffset.UtcNow; saved.ResponseReason = "Không đủ nhân lực";
        await db.SaveChangesAsync();
        await using var concurrent = database.CreateContext();
        var first = await db.Employees.SingleAsync(e => e.Id == employee.Id);
        var second = await concurrent.Employees.SingleAsync(e => e.Id == employee.Id);
        first.UpdatedAt = DateTimeOffset.UtcNow;
        second.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => concurrent.SaveChangesAsync());
    }
}
