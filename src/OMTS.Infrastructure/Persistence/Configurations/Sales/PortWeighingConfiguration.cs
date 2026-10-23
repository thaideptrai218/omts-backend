using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PortWeighingConfiguration : IEntityTypeConfiguration<PortWeighing>
{
    public void Configure(EntityTypeBuilder<PortWeighing> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.Weighings).HasForeignKey(x => x.OrderId);
        builder.Property(x => x.Port).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ConfirmedKg).HasPrecision(18, 3);
        builder.Property(x => x.BuyerConfirmation).HasMaxLength(Lengths.Reason);
    }
}
