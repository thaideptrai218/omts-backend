using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal static class Lengths
{
    public const int Code = 50;
    public const int ShortText = 100;
    public const int Name = 200;
    public const int Address = 500;
    public const int Phone = 20;
    public const int Email = 256;
    public const int Url = 1000;
    public const int Reason = 1000;
    public const int LongText = 2000;
    public const int Hash = 128;
}

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

    /// <summary>Money columns hold VND as multiples of 1,000 (BR-13, NFR-06).</summary>
    public static void HasThousandVndCheck<T>(this EntityTypeBuilder<T> builder, string column)
        where T : class
    {
        builder.ToTable(t => t.HasCheckConstraint($"CK_{typeof(T).Name}_{column}_Thousands", $"[{column}] % 1000 = 0"));
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

    public static string ExactlyOneNotNull(params string[] columns) =>
        "(" + string.Join(" + ", columns.Select(c => $"CASE WHEN [{c}] IS NULL THEN 0 ELSE 1 END")) + ") = 1";
}
