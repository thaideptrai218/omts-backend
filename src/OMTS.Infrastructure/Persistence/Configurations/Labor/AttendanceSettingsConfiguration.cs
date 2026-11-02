using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceSettingsConfiguration : IEntityTypeConfiguration<AttendanceSettings>
{
    public void Configure(EntityTypeBuilder<AttendanceSettings> builder)
    {
        builder.HasIndex(x => x.EffectiveFrom).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_AttendanceSettings_Values",
            "([RoundingMinutes] IN (0, 15, 30, 60)) AND (([BreakFrom] IS NULL AND [BreakTo] IS NULL) OR ([BreakFrom] IS NOT NULL AND [BreakTo] IS NOT NULL AND [BreakTo] > [BreakFrom]))"));
    }
}
