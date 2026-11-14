using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>Builds the answers. Only staff get the profile, the prices and the sendings; the photo links are signed for one hour, as on the provenance page.</summary>
public sealed class QualityClaimViews(IQualityClaimRepository claims, IFileStorage storage, IStatusCatalog statusCatalog)
{
    private static readonly TimeSpan PhotoLinkLifetime = TimeSpan.FromHours(1);

    public QualityClaimRow Row(QualityClaim c, bool forStaff) => new(
        c.Id, c.Code, c.SentAt, c.StatusId, statusCatalog.DisplayNameOf(c.StatusId), c.Description, c.Photos.Count, c.Offer, c.OfferAmount,
        new QualityClaimInvoiceRef(c.InvoiceId, c.Invoice.Code),
        forStaff ? new QualityClaimCustomerView(c.CustomerId, c.Customer.Name, c.Customer.PhoneNumber) : null);

    public async Task<QualityClaimView> ForStaffAsync(QualityClaim claim, CancellationToken cancellationToken)
    {
        var invoice = claim.Invoice;
        var sendings = await claims.ListSendingsAsync(claim.Id, cancellationToken);
        return new QualityClaimView(
            claim.Id, claim.Code, claim.SentAt, claim.StatusId, statusCatalog.DisplayNameOf(claim.StatusId), claim.Description, await PhotosAsync(claim, cancellationToken), claim.Offer, claim.OfferAmount,
            Response(claim), Answer(claim),
            new QualityClaimCustomerView(claim.CustomerId, claim.Customer.Name, claim.Customer.PhoneNumber),
            new QualityClaimInvoiceView(
                invoice.Id, invoice.Code, invoice.InvoiceDate, invoice.ZoneId, invoice.Zone.Name, invoice.StatusId, statusCatalog.DisplayNameOf(invoice.StatusId),
                invoice.Subtotal, invoice.DiscountAmount, invoice.OtherCharges, invoice.PriceCutTotal, invoice.Total,
                Lines(invoice, withPrices: true),
                invoice.PriceCuts.OrderBy(p => p.CutDate).Select(p => new QualityClaimPriceCutView(p.CutDate, p.Amount, p.Reason)).ToList()),
            sendings.Select(s => new QualityClaimSendingView(
                s.Channel.ToString(), s.PhoneNumber, s.DeliveryStatusId, s.DeliveryStatusId is { } id ? statusCatalog.DisplayNameOf(id) : null, s.DeliveryStatusAt)).ToList(),
            RowVersionGuard.Encode(claim.RowVersion));
    }

    public async Task<TraderQualityClaimView> ForTraderAsync(QualityClaim claim, CancellationToken cancellationToken) => new(
        claim.Id, claim.Code, claim.SentAt, claim.StatusId, statusCatalog.DisplayNameOf(claim.StatusId), claim.Description, await PhotosAsync(claim, cancellationToken), claim.Offer, claim.OfferAmount,
        Response(claim), Answer(claim), new QualityClaimInvoiceRef(claim.InvoiceId, claim.Invoice.Code), claim.Invoice.InvoiceDate, claim.Invoice.Total, Lines(claim.Invoice, withPrices: false));

    private static QualityClaimResponseView? Response(QualityClaim c) => c.RespondedAt is { } at ? new QualityClaimResponseView(c.ResponseNote, at) : null;

    private static QualityClaimAnswerView? Answer(QualityClaim c) => c.AnsweredAt is { } at ? new QualityClaimAnswerView(c.AnswerNote, at) : null;

    private static List<QualityClaimInvoiceLineView> Lines(SalesInvoice invoice, bool withPrices) => invoice.Lines
        .OrderBy(l => l.LineNumber)
        .Select(l => new QualityClaimInvoiceLineView(l.Product.Name, l.HarvestLot.LotCode, l.Kg, withPrices ? l.UnitPrice : null, withPrices ? l.Amount : null))
        .ToList();

    private async Task<IReadOnlyList<QualityClaimPhotoView>> PhotosAsync(QualityClaim claim, CancellationToken cancellationToken)
    {
        var photos = new List<QualityClaimPhotoView>();
        foreach (var photo in claim.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new QualityClaimPhotoView(photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, PhotoLinkLifetime, cancellationToken)));
        }

        return photos;
    }
}
