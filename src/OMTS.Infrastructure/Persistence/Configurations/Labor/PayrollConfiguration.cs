using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayrollConfiguration : IEntityTypeConfiguration<Payroll>
{
    public void Configure(EntityTypeBuilder<Payroll> builder)
    {
        builder.Property(x => x.Period).HasMaxLength(7);
        builder.Property(x => x.ReturnReason).HasMaxLength(Lengths.Reason);
        builder.ReferencesAccount(
            nameof(Payroll.PreparedByAccountId), nameof(Payroll.ReturnedByAccountId), nameof(Payroll.FinalizedByAccountId));

        // One payroll per month (LAB-07): two parallel creations cannot both pass the pre-check.
        builder.HasIndex(x => x.Period).IsUnique();
    }
}
