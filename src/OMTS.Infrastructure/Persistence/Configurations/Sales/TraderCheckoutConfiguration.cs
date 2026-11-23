using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class TraderCheckoutConfiguration : IEntityTypeConfiguration<TraderCheckout>
{
    public void Configure(EntityTypeBuilder<TraderCheckout> builder)
    {
        // One checkout per payment waiting for its transfer.
        builder.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId);
        builder.HasIndex(x => x.PaymentId).IsUnique();
        builder.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId);
        // A new checkout looks for the newest one of the invoice still being paid.
        builder.HasIndex(x => new { x.InvoiceId, x.CreatedAt });
        builder.ReferencesAccount(nameof(TraderCheckout.AccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        // The payOS notification names the checkout by its order code.
        builder.HasIndex(x => x.PayOsOrderCode).IsUnique();
        builder.Property(x => x.PayOsPaymentLinkId).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.QrContent).HasMaxLength(Lengths.Url);
        builder.Property(x => x.BankBin).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.AccountNumber).HasMaxLength(Lengths.Code);
        builder.Property(x => x.AccountName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TransferContent).HasMaxLength(Lengths.Code);
        builder.Property(x => x.ZaloOrderId).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.ZaloLastError).HasMaxLength(Lengths.Address);
        // The retry job reads the paid orders Zalo has not accepted yet.
        builder.HasIndex(x => new { x.ZaloReportedAt, x.ZaloOrderId });
        builder.ToTable(t => t.HasCheckConstraint("CK_TraderCheckout_Amount", "[Amount] > 0 AND [ZaloReportAttempts] >= 0"));
    }
}
