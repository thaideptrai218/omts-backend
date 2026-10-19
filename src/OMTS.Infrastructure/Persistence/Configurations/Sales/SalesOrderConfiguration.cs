using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();

        builder.HasOne(x => x.Customer).WithMany(c => c.Orders).HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.SourceLot).WithMany().HasForeignKey(x => x.SourceLotId);
        builder.HasOne(x => x.SourceReturn).WithMany(r => r.ResaleOrders).HasForeignKey(x => x.SourceReturnId);
        builder.ReferencesAccount(nameof(SalesOrder.RecordedByAccountId));

        builder.Property(x => x.RequestedQuantity).HasPrecision(18, 3);
        builder.Property(x => x.QuantityAllocatedFromReturn).HasPrecision(18, 3);
        builder.Property(x => x.OriginalTerms).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.ChannelEvidence).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.TraderProposedChanges).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.AgreementChannel).HasMaxLength(Lengths.Code);
        builder.Property(x => x.AgreementBasis).HasMaxLength(Lengths.Reason);

        builder.HasIndex(x => new { x.ZoneId, x.StatusId });
        builder.HasIndex(x => new { x.CustomerId, x.StatusId });
    }
}
