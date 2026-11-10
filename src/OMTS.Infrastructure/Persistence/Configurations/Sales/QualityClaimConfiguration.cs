using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class QualityClaimConfiguration : IEntityTypeConfiguration<QualityClaim>
{
    public void Configure(EntityTypeBuilder<QualityClaim> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId);
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(QualityClaim.RespondedByAccountId));
        builder.Property(x => x.Description).HasMaxLength(QualityClaim.MaxDescriptionLength);
        builder.Property(x => x.ResponseNote).HasMaxLength(Lengths.Reason);

        // BR-CLAIM-01: at most one claim Awaiting response per invoice; an answered claim does not stop a new one.
        builder.HasIndex(x => x.InvoiceId, "UX_QualityClaims_Invoice_Awaiting")
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.QualityClaim.AwaitingResponse}");
        builder.HasIndex(x => new { x.CustomerId, x.SentAt });
        builder.HasIndex(x => new { x.StatusId, x.SentAt });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_QualityClaim_Rejected_Has_Note", $"[StatusId] <> {StatusIds.QualityClaim.Rejected} OR [ResponseNote] IS NOT NULL");
            t.HasCheckConstraint("CK_QualityClaim_Accepted_Has_Offer", $"[StatusId] <> {StatusIds.QualityClaim.Accepted} OR [Offer] IS NOT NULL");
            t.HasCheckConstraint(
                "CK_QualityClaim_Answered_Has_Response",
                $"[StatusId] = {StatusIds.QualityClaim.AwaitingResponse} OR ([RespondedAt] IS NOT NULL AND [RespondedByAccountId] IS NOT NULL)");
        });
    }
}

internal sealed class QualityClaimPhotoConfiguration : IEntityTypeConfiguration<QualityClaimPhoto>
{
    public void Configure(EntityTypeBuilder<QualityClaimPhoto> builder)
    {
        builder.HasOne(x => x.Claim).WithMany(c => c.Photos).HasForeignKey(x => x.ClaimId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots.
        builder.HasIndex(x => new { x.ClaimId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_QualityClaimPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {QualityClaim.MaxPhotos}"));
    }
}
