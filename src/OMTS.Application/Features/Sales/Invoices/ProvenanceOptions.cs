namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>"Provenance" section: where the public provenance page (ORIGIN-01, SCR-ORIGIN-01) of the web app is served, for the link of the QR code of an invoice.</summary>
public sealed class ProvenanceOptions
{
    public const string SectionName = "Provenance";

    /// <summary>The origin of the web app, e.g. https://omts.example.vn; set in every deployed environment (the link goes into the SMS of the invoice).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>The link the QR code encodes and MSG-SALE-15 carries: {PublicBaseUrl}/origin/{qrToken} (BR-ORIGIN-01).</summary>
    public string LinkOf(string qrToken) => $"{PublicBaseUrl.TrimEnd('/')}/origin/{qrToken}";
}
