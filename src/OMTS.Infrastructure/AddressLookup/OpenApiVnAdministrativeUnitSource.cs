using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Addresses;

namespace OMTS.Infrastructure.AddressLookup;

/// <summary>
/// provinces.open-api.vn, version 2 (the two-level list since 1/7/2025): <c>p/</c> lists the provinces, <c>p/{code}?depth=2</c> a province with its wards. Only a province
/// code is sent. A malformed entry is skipped; an answer with no usable province counts as a failure.
/// </summary>
internal sealed class OpenApiVnAdministrativeUnitSource(HttpClient http, ILogger<OpenApiVnAdministrativeUnitSource> logger) : IAdministrativeUnitSource
{
    private const string Service = "Administrative units list";
    private const int MaxNameLength = 200;

    public async Task<IReadOnlyList<ProvinceUnit>?> GetProvincesAsync(CancellationToken cancellationToken)
    {
        var (_, body) = await LookupHttp.GetJsonAsync(http, "p/", Service, logger, cancellationToken);
        using (body)
        {
            if (body is null || body.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var provinces = body.RootElement.EnumerateArray()
                .Select(e => Unit(e) is { } u ? new ProvinceUnit(u.Code, u.Name, u.DivisionType) : null)
                .OfType<ProvinceUnit>().ToList();
            return provinces.Count > 0 ? provinces : null;
        }
    }

    public async Task<IReadOnlyList<WardUnit>?> GetWardsAsync(int provinceCode, CancellationToken cancellationToken)
    {
        var (status, body) = await LookupHttp.GetJsonAsync(
            http, $"p/{provinceCode.ToString(CultureInfo.InvariantCulture)}?depth=2", Service, logger, cancellationToken);
        using (body)
        {
            if (status == HttpStatusCode.NotFound)
            {
                return [];
            }

            if (body is null || body.RootElement.ValueKind != JsonValueKind.Object
                || !body.RootElement.TryGetProperty("wards", out var wards) || wards.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return wards.EnumerateArray()
                .Select(e => Unit(e) is { } u ? new WardUnit(u.Code, u.Name, u.DivisionType, ProvinceCodeOf(e) ?? provinceCode) : null)
                .OfType<WardUnit>().ToList();
        }
    }

    private static (int Code, string Name, string DivisionType)? Unit(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number) || number <= 0
            || Text(element, "name") is not { Length: > 0 and <= MaxNameLength } name)
        {
            return null;
        }

        return (number, name, Text(element, "division_type") ?? string.Empty);
    }

    private static int? ProvinceCodeOf(JsonElement element) =>
        element.TryGetProperty("province_code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
}
