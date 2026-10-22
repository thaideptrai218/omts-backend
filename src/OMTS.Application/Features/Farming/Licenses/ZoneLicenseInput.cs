namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Checked FARM-03 fields of a create, edit or replace request.</summary>
internal sealed record ZoneLicenseInput(
    string DocumentType,
    string DocumentNumber,
    DateOnly EffectiveDate,
    DateOnly? ExpiryDate,
    string? IssuingAuthority,
    string? Holder,
    string? LegalScope,
    IReadOnlyList<Guid> ZoneIds)
{
    /// <summary>
    /// Validates the request; <paramref name="defaults"/> (the replaced license) fills an empty type or zone
    /// list on replace. Zone existence is checked by the caller.
    /// </summary>
    public static (ZoneLicenseInput Input, IReadOnlyList<string> Invalid) Read(SaveZoneLicenseRequest request, ZoneLicenseInput? defaults = null)
    {
        var check = new FieldCheck();
        var type = string.IsNullOrWhiteSpace(request.DocumentType) && defaults is not null
            ? defaults.DocumentType
            : check.Required(request.DocumentType, "Document type", 100).ToUpperInvariant();
        check.Check(LicenseTypes.Names.ContainsKey(type), "Document type");

        var number = check.Required(request.DocumentNumber, "Document number", 100);
        check.Check(request.EffectiveDate is not null, "Effective date");
        check.Check(request.ExpiryDate is null || request.EffectiveDate is null || request.ExpiryDate > request.EffectiveDate, "Expiry date");
        var authority = check.Optional(request.IssuingAuthority, "Issuing authority", 200);
        var holder = check.Optional(request.Holder, "Holder", 200);
        var scope = check.Optional(request.LegalScope, "Legal scope", 1000);

        var zoneIds = (request.ZoneIds is { Count: > 0 } ? request.ZoneIds : defaults?.ZoneIds ?? []).Distinct().ToList();
        check.Check(zoneIds.Count > 0 && !zoneIds.Contains(Guid.Empty), "Zones");

        var input = new ZoneLicenseInput(type, number, request.EffectiveDate ?? default, request.ExpiryDate, authority, holder, scope, zoneIds);
        return (input, check.Invalid.Distinct().ToList());
    }
}
