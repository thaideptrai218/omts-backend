using Microsoft.EntityFrameworkCore;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Rows for a migration test that stands on an older schema. The current model writes every column it has, also those a later migration adds, so a table that gained columns since is written by hand.
/// </summary>
public static class OldSchemaRows
{
    /// <summary>A cooperative with the columns every schema since CooperativeProfileV2 has: <c>Cooperatives</c> gains the harvest settings in HarvestLots.</summary>
    public static async Task<Guid> CooperativeAsync(OmtsDbContext db)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({id}, N'HTX', SYSDATETIMEOFFSET())");
        return id;
    }
}
