using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayRuleVersionConfiguration : IEntityTypeConfiguration<PayRuleVersion>
{
    public void Configure(EntityTypeBuilder<PayRuleVersion> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.ReferencesAccount(nameof(PayRuleVersion.EnteredByAccountId), nameof(PayRuleVersion.ApprovedByAccountId));
        builder.OwnsMany(x => x.Components, component =>
        {
            component.ToJson();
            component.Property(c => c.Name).HasMaxLength(Lengths.ShortText);
        });

        // Versions of a person never overlap, so two cannot start on the same day.
        builder.HasIndex(x => new { x.EmployeeId, x.ValidFrom }).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasIndex(x => new { x.WorkerId, x.ValidFrom }).IsUnique().HasFilter("[WorkerId] IS NOT NULL");
        builder.HasThousandVndCheck(nameof(PayRuleVersion.Amount));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_PayRuleVersion_OnePerson",
            ConfigurationHelpers.ExactlyOneNotNull(nameof(PayRuleVersion.EmployeeId), nameof(PayRuleVersion.WorkerId))));
    }
}
