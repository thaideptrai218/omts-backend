using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Finance;

namespace OMTS.Infrastructure.Persistence.Configurations.Finance;

internal sealed class CustomerCreditConfiguration : IEntityTypeConfiguration<CustomerCredit>
{
    public void Configure(EntityTypeBuilder<CustomerCredit> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.SourceSale).WithMany().HasForeignKey(x => x.SourceSaleId);
        builder.HasOne(x => x.SourceAdjustment).WithMany().HasForeignKey(x => x.SourceAdjustmentId);
        builder.HasOne(x => x.SourceReceipt).WithMany().HasForeignKey(x => x.SourceReceiptId);
        builder.HasThousandVndCheck(nameof(CustomerCredit.Amount));
    }
}
