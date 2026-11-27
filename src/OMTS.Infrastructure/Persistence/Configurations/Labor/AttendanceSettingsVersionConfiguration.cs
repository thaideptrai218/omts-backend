using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceSettingsVersionConfiguration : IEntityTypeConfiguration<AttendanceSettingsVersion>
{
    public void Configure(EntityTypeBuilder<AttendanceSettingsVersion> builder)
    {
        builder.HasIndex(x => x.EffectiveFrom).IsUnique();
        builder.ReferencesAccount(nameof(AttendanceSettingsVersion.EnteredByAccountId));
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_AttendanceSettingsVersions_Break",
                "([BreakStart] IS NULL AND [BreakEnd] IS NULL) OR ([BreakStart] IS NOT NULL AND [BreakEnd] IS NOT NULL AND [BreakEnd] > [BreakStart])");
            t.HasCheckConstraint(
                "CK_AttendanceSettingsVersions_Shift",
                "[ShiftMinutes] BETWEEN 30 AND 720 AND [ShiftToleranceMinutes] >= 0 AND [ShiftToleranceMinutes] * 2 <= [ShiftMinutes]");
        });
    }
}
