namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Checked FARM-03 fields of a create, edit or replace request.</summary>
internal sealed record ZoneLicenseInput(
    string? DocumentType,
    string DocumentNumber,
    DateOnly EffectiveDate,
    DateOnly ExpiryDate,
    string? IssuingAuthority,
    string? Holder,
    string? LegalScope,
    Guid ZoneId)
{
    /// <summary>
    /// Validates the request (MSG-GEN-01 fields): number, effective date and an expiry date after it are required; the
    /// type, authority, holder and legal scope are optional. <paramref name="zoneOfLicense"/> is the zone of the license
    /// being edited or replaced: the zone may then be left out, and another zone is refused. <paramref name="defaultType"/>
    /// fills an empty type on replace. Zone existence and status are checked by the caller.
    /// </summary>
    public static (ZoneLicenseInput Input, IReadOnlyList<string> Invalid) Read(
        SaveZoneLicenseRequest request, Guid? zoneOfLicense = null, string? defaultType = null)
    {
        var check = new FieldCheck();
        var type = check.Optional(request.DocumentType, "Document type", 100)?.ToUpperInvariant() ?? defaultType;
        check.Check(type is null || LicenseTypes.Names.ContainsKey(type), "Document type");

        var number = check.Required(request.DocumentNumber, "Document number", 100);
        check.Check(request.EffectiveDate is not null, "Effective date");
        check.Check(request.ExpiryDate is not null && (request.EffectiveDate is null || request.ExpiryDate > request.EffectiveDate), "Expiry date");
        var authority = check.Optional(request.IssuingAuthority, "Issuing authority", 200);
        var holder = check.Optional(request.Holder, "Holder", 200);
        var scope = check.Optional(request.LegalScope, "Legal scope", 1000);

        var zoneId = RequestedZone(request, check) ?? zoneOfLicense;
        check.Check(zoneId is { } id && id != Guid.Empty && (zoneOfLicense is null || id == zoneOfLicense), "Zone");

        var input = new ZoneLicenseInput(
            type, number, request.EffectiveDate ?? default, request.ExpiryDate ?? default, authority, holder, scope, zoneId ?? Guid.Empty);
        return (input, check.Invalid.Distinct().ToList());
    }

    /// <summary><c>zoneId</c>, or the single value of the deprecated <c>zoneIds</c>; several zones are refused.</summary>
    private static Guid? RequestedZone(SaveZoneLicenseRequest request, FieldCheck check)
    {
        if (request.ZoneId is { } zoneId)
        {
            return zoneId;
        }

        var listed = request.ZoneIds?.Distinct().ToList() ?? [];
        check.Check(listed.Count <= 1, "Zone");
        return listed.Count == 1 ? listed[0] : null;
    }
}
