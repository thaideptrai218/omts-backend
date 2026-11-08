using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PendingSaleConfiguration : IEntityTypeConfiguration<PendingSale>
{
    public void Configure(EntityTypeBuilder<PendingSale> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        // The bank notification finds the sale by the code of its transfer content (GFR-08).
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId);
        builder.ReferencesAccount(nameof(PendingSale.CreatedByAccountId));
        builder.ReferencesAccount(nameof(PendingSale.EndedByAccountId));
        builder.Property(x => x.Total).HasPrecision(18, 0);
        builder.Property(x => x.CashAmount).HasPrecision(18, 0);
        builder.Property(x => x.ToPayAmount).HasPrecision(18, 0);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        // The sales still awaiting their transfer, by zone (the list of the port).
        builder.HasIndex(x => new { x.Status, x.ZoneId });
        builder.ToTable(t => t.HasCheckConstraint("CK_PendingSale_Amounts", "[Total] >= 0 AND [CashAmount] >= 0 AND [ToPayAmount] > 0 AND [CashAmount] + [ToPayAmount] = [Total]"));
    }
}
