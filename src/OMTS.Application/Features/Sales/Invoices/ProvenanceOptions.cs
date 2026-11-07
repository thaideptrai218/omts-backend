namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>"Provenance" section: where the public provenance page (ORIGIN-01, SCR-ORIGIN-01) of the web app is served, for the link of the QR code of an invoice.</summary>
public sealed class ProvenanceOptions
{
    public const string SectionName = "Provenance";

    public const string InvalidPublicBaseUrl = $"{SectionName}:PublicBaseUrl must be the absolute http or https URL of the web app, e.g. https://<web host>.";

    /// <summary>
    /// The origin of the web app, e.g. https://omts.example.vn. Required in every environment: the host does not start without an absolute http(s) URL, so a QR code or an
    /// SMS never carries a relative link that no trader can open (BR-ORIGIN-01, BR-TRACE-02: a QR code is never made again).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>The link the QR code encodes and MSG-SALE-15 carries: {PublicBaseUrl}/origin/{qrToken} (BR-ORIGIN-01).</summary>
    public string LinkOf(string qrToken) => $"{PublicBaseUrl.TrimEnd('/')}/origin/{qrToken}";

    /// <summary>The check run at startup: an absolute http or https URL with a host.</summary>
    public bool HasWebAddress() =>
        Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0;
}
