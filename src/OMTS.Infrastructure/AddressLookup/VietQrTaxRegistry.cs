using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Addresses;

namespace OMTS.Infrastructure.AddressLookup;

/// <summary>
/// The business registry of the tax authority through VietQR: <c>business/{taxCode}</c>. Only the tax code is sent. It answers HTTP 200 with <c>code</c> "00" and the
/// company, or "51" for a tax code nobody holds; any other code (a limit, a fault of the service) counts as unavailable. The answer is not logged.
/// </summary>
internal sealed class VietQrTaxRegistry(HttpClient http, ILogger<VietQrTaxRegistry> logger) : ITaxRegistry
{
    private const string Service = "VietQR business registry";
    private const string Success = "00";
    private const string NotFound = "51";
    private const int MaxTextLength = 500;

    public async Task<TaxRegistryLookup> FindAsync(string taxCode, CancellationToken cancellationToken)
    {
        // The caller has checked the format; this keeps anything else out of the path.
        if (!taxCode.All(c => char.IsAsciiDigit(c) || c == '-'))
        {
            return TaxRegistryLookup.NotFound;
        }

        var (_, body) = await LookupHttp.GetJsonAsync(http, $"business/{taxCode}", Service, logger, cancellationToken);
        using (body)
        {
            if (body is null || body.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TaxRegistryLookup.Unavailable;
            }

            var root = body.RootElement;
            var code = Text(root, "code");
            if (code == NotFound)
            {
                return TaxRegistryLookup.NotFound;
            }

            if (code != Success)
            {
                logger.LogWarning("{Service} answered code {Code}", Service, code);
                return TaxRegistryLookup.Unavailable;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || Text(data, "name") is not { Length: > 0 } name)
            {
                return TaxRegistryLookup.Unavailable;
            }

            return new TaxRegistryLookup(
                TaxRegistryOutcome.Found,
                new TaxRegistryCompany(Text(data, "id") is { Length: > 0 } id ? id : taxCode, name, Text(data, "shortName"), Text(data, "address") ?? string.Empty, Text(data, "status") ?? string.Empty));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text
            ? (text.Length > MaxTextLength ? text[..MaxTextLength] : text)
            : null;
}
