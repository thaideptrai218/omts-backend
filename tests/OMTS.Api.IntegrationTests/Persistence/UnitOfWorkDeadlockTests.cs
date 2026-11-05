using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Persistence;

public class UnitOfWorkDeadlockTests
{
    // Two transactions update the same two rows in opposite order. SQL Server rolls back the one with the low priority:
    // that save must end as an edit conflict (409 MSG06), not as an unexpected error (500).
    [SqlServerFact]
    public async Task A_deadlock_victim_gets_the_edit_conflict_not_a_server_error()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var victim = database.CreateContext();
        await using var other = database.CreateContext();
        await victim.Database.MigrateAsync();
        var (first, second) = (StatusIds.WaterMeasurement.Active, StatusIds.WaterMeasurement.Voided);
        var status = await victim.Statuses.SingleAsync(s => s.Id == first);
        status.Meaning += " (changed)";

        await using var otherTransaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlAsync($"UPDATE Statuses SET Meaning = Meaning WHERE Id = {first}");
        await using var victimTransaction = await victim.Database.BeginTransactionAsync();
        await victim.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW");
        await victim.Database.ExecuteSqlAsync($"UPDATE Statuses SET Meaning = Meaning WHERE Id = {second}");

        // Waits for the row the other transaction holds, then the other transaction waits for this one's row.
        var save = new UnitOfWork(victim).SaveChangesAsync();
        await Task.Delay(TimeSpan.FromSeconds(1));
        await other.Database.ExecuteSqlAsync($"UPDATE Statuses SET Meaning = Meaning WHERE Id = {second}");

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => save);
        await otherTransaction.CommitAsync();
    }
}
