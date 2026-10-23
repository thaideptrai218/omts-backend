using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PortHandoverConfiguration : IEntityTypeConfiguration<PortHandover>
{
    public void Configure(EntityTypeBuilder<PortHandover> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.Handovers).HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.Weighing).WithMany().HasForeignKey(x => x.WeighingId);
        builder.HasOne(x => x.PublicationAtHandover).WithMany().HasForeignKey(x => x.PublicationIdAtHandover);
        builder.Property(x => x.DeliveredBy).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ReceivedBy).HasMaxLength(Lengths.Name);
    }
}
