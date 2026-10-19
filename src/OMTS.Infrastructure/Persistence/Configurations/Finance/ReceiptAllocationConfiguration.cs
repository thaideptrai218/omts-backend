using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Finance;

namespace OMTS.Infrastructure.Persistence.Configurations.Finance;

internal sealed class ReceiptAllocationConfiguration : IEntityTypeConfiguration<ReceiptAllocation>
{
    public void Configure(EntityTypeBuilder<ReceiptAllocation> builder)
    {
        builder.HasOne(x => x.Receipt).WithMany(r => r.Allocations).HasForeignKey(x => x.ReceiptId);
        builder.HasOne(x => x.Sale).WithMany(s => s.ReceiptAllocations).HasForeignKey(x => x.SaleId);
        builder.ToTable(t => t.HasCheckConstraint("CK_ReceiptAllocation_Amount_Positive", "[AllocatedAmount] > 0"));
        builder.HasThousandVndCheck(nameof(ReceiptAllocation.AllocatedAmount));
    }
}
