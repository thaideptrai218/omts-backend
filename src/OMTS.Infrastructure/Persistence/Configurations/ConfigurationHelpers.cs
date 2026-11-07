using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal static class ConfigurationHelpers
{
    /// <summary>
    /// Actor columns (recorded/approved/decided by…) reference Account without a navigation property.
    /// </summary>
    public static void ReferencesAccount<T>(this EntityTypeBuilder<T> builder, params string[] foreignKeys)
        where T : class
    {
        foreach (var foreignKey in foreignKeys)
        {
            builder.HasOne<Account>().WithMany().HasForeignKey(foreignKey);
        }
    }

    /// <summary>Latitude/Longitude columns hold a valid WGS84 point, both set or both empty.</summary>
    public static void HasCoordinateChecks<T>(this EntityTypeBuilder<T> builder)
        where T : class
    {
        var table = typeof(T).Name;
        builder.ToTable(t => t.HasCheckConstraint(
            $"CK_{table}_Coordinates",
            "([Latitude] IS NULL AND [Longitude] IS NULL) OR " +
            "([Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)"));
    }
}
