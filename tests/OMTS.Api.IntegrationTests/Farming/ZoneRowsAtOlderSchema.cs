using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// Writes a cooperative and zones into a database migrated to an older schema. The current model cannot do it: it holds
/// columns that later migrations add to the zone table, so the insert names only the columns every schema has.
/// </summary>
internal static class ZoneRowsAtOlderSchema
{
    public static async Task<Guid> AddCooperativeAsync(DbContext db)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({id}, N'HTX', SYSDATETIMEOFFSET())");
        return id;
    }

    public static async Task<Guid> AddZoneAsync(DbContext db, Guid cooperativeId, string code, string name)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt)
            VALUES ({id}, {cooperativeId}, {code}, {name}, {StatusIds.Usage.InUse}, SYSDATETIMEOFFSET())
            """);
        return id;
    }
}
