using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeLoginCodeConfiguration : IEntityTypeConfiguration<EmployeeLoginCode>
{
    public void Configure(EntityTypeBuilder<EmployeeLoginCode> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.IssuedBy).WithMany().HasForeignKey(x => x.IssuedByAccountId);
        builder.Property(x => x.CodeHash).HasMaxLength(Lengths.Hash);
        builder.Property(x => x.RowVersion).IsRowVersion();
        // One open code per employee (BR-LABOR-02: a new code cancels the earlier one): two Zone Managers issuing at the same time cannot both leave one open.
        builder.HasIndex(x => x.EmployeeId)
            .IsUnique()
            .HasDatabaseName(EmployeeAppConfigurations.OneOpenLoginCodePerEmployee)
            .HasFilter("[ConsumedAt] IS NULL AND [CancelledAt] IS NULL");
    }
}
