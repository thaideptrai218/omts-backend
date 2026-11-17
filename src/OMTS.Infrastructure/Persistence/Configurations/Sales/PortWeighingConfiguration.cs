using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PortWeighingConfiguration : IEntityTypeConfiguration<PortWeighing>
{
    public void Configure(EntityTypeBuilder<PortWeighing> builder)
    {
        builder.HasOne(x => x.PurchaseRequest).WithMany().HasForeignKey(x => x.PurchaseRequestId);
        builder.ReferencesAccount(nameof(PortWeighing.WeighedByAccountId));
        builder.ReferencesAccount(nameof(PortWeighing.ConfirmedByAccountId));
        builder.Ignore(x => x.IsConfirmed);
        builder.HasIndex(x => new { x.PurchaseRequestId, x.WeighedAt });
        // One weighing of a request waits for the trader at a time, so a second tap on Save weighs nothing twice.
        builder.HasIndex(x => x.PurchaseRequestId, "UX_PortWeighing_One_Unconfirmed_Per_Request").IsUnique().HasFilter("[ConfirmedAt] IS NULL");
    }
}
