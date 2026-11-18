using System.Text.RegularExpressions;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Parties;

/// <summary>
/// A picked address: the codes of the administrative units list with their names, and the street (house number and street name). A name with no code
/// is an old free-text entry that has not been matched to the list yet.
/// </summary>
public sealed record AddressParts(int? ProvinceCode, string? ProvinceName, int? WardCode, string? WardName, string? Street)
{
    public static readonly AddressParts Empty = new(null, null, null, null, null);

    /// <summary>There is a province or ward name that no code stands behind.</summary>
    public bool NeedsReview => (ProvinceName is not null && ProvinceCode is null) || (WardName is not null && WardCode is null);

    /// <summary>Province, ward and street are all given. An old entry kept as text counts: it saves, and <see cref="NeedsReview"/> asks for a new pick.</summary>
    public bool IsComplete => ProvinceName is not null && WardName is not null && Street is not null;
}

/// <summary>The invoice entries of a request, as sent.</summary>
public sealed record InvoiceInfoInput(
    InvoiceBuyerType? BuyerType, string? Name, string? TaxCode, string? PersonalTaxId, bool? AddressSameAsContact, AddressParts Address, string? Email);

/// <summary>The invoice entries after the checks: what the buyer type does not use is empty.</summary>
public sealed record InvoiceInfo(
    InvoiceBuyerType BuyerType, string? Name, string? TaxCode, string? PersonalTaxId, bool AddressSameAsContact, AddressParts Address, string? Email);

/// <summary>
/// The e-invoice rules of a customer and a supplier. Organization: name, address, tax code and email are required. Individual: name and address are required; the
/// personal tax number (or ID number) and the email are optional. Consumer (customers only): nothing is kept. An address is required as province, ward and
/// street (an old entry kept as text counts). The invoice address may be the contact address. The checks that need the administrative units list (the ward belongs to the
/// province) are <see cref="PartyAddressVerifier"/>'s.
/// </summary>
internal static partial class PartyInvoiceRules
{
    public const int MaxNameLength = 200;
    public const int MaxPlaceLength = 100;
    public const int MaxStreetLength = 500;

    /// <summary>The contact address of a request: it is optional, but what is given must be consistent.</summary>
    public static AddressParts Contact(int? provinceCode, string? provinceName, int? wardCode, string? wardName, string? street, List<string> invalid) =>
        Address(provinceCode, provinceName, wardCode, wardName, street, "Province", "Ward", "Address", invalid);

    public static InvoiceInfo Invoice(InvoiceInfoInput input, AddressParts contact, InvoiceBuyerType fallback, bool allowConsumer, List<string> invalid)
    {
        var type = input.BuyerType ?? fallback;
        if (!Enum.IsDefined(type) || (type == InvoiceBuyerType.Consumer && !allowConsumer))
        {
            Add(invalid, "Invoice buyer type");
            type = fallback;
        }

        if (type == InvoiceBuyerType.Consumer)
        {
            return new InvoiceInfo(type, null, null, null, true, AddressParts.Empty, null);
        }

        var name = DisplayNames.Normalize(input.Name);
        if (name.Length is 0 or > MaxNameLength)
        {
            Add(invalid, "Invoice name");
        }

        string? taxCode = null;
        string? personalTaxId = null;
        if (type == InvoiceBuyerType.Organization)
        {
            taxCode = input.TaxCode?.Trim();
            if (string.IsNullOrEmpty(taxCode) || !OrganizationTaxCode().IsMatch(taxCode))
            {
                Add(invalid, "Tax code");
            }
        }
        else
        {
            personalTaxId = Optional(input.PersonalTaxId);
            if (personalTaxId is not null && !PersonalTaxNumber().IsMatch(personalTaxId))
            {
                Add(invalid, "Personal tax ID");
            }
        }

        var email = Optional(input.Email);
        if (email is not null)
        {
            email = EmailAddresses.Normalize(email);
        }

        if (email is null && (type == InvoiceBuyerType.Organization || !string.IsNullOrWhiteSpace(input.Email)))
        {
            Add(invalid, "Invoice email");
        }

        var same = input.AddressSameAsContact ?? true;
        var address = AddressParts.Empty;
        if (same)
        {
            if (!contact.IsComplete)
            {
                Add(invalid, contact.ProvinceName is null ? "Province" : contact.WardName is null ? "Ward" : "Address");
            }
        }
        else
        {
            address = Address(
                input.Address.ProvinceCode, input.Address.ProvinceName, input.Address.WardCode, input.Address.WardName, input.Address.Street,
                "Invoice province", "Invoice ward", "Invoice street", invalid);
            if (!address.IsComplete)
            {
                Add(invalid, address.ProvinceName is null ? "Invoice province" : address.WardName is null ? "Invoice ward" : "Invoice street");
            }
        }

        return new InvoiceInfo(type, name.Length == 0 ? null : name, taxCode, personalTaxId, same, address, email);
    }

    private static AddressParts Address(
        int? provinceCode, string? provinceName, int? wardCode, string? wardName, string? street,
        string provinceField, string wardField, string streetField, List<string> invalid)
    {
        var province = Optional(provinceName);
        var ward = Optional(wardName);
        var text = Optional(street);
        if (provinceCode is <= 0 || (provinceCode is not null && province is null) || province?.Length > MaxPlaceLength)
        {
            Add(invalid, provinceField);
        }

        // A ward is chosen within a province.
        if (wardCode is <= 0 || (wardCode is not null && (ward is null || provinceCode is null)) || ward?.Length > MaxPlaceLength)
        {
            Add(invalid, wardField);
        }

        if (text?.Length > MaxStreetLength)
        {
            Add(invalid, streetField);
        }

        return new AddressParts(provinceCode, province, wardCode, ward, text);
    }

    /// <summary>The first entry that is not blank: an older client and the address picker name the same entry differently.</summary>
    public static string? First(string? preferred, string? other) => string.IsNullOrWhiteSpace(preferred) ? other : preferred;

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(List<string> invalid, string field)
    {
        if (!invalid.Contains(field))
        {
            invalid.Add(field);
        }
    }

    /// <summary>10 digits, optionally with a hyphen and 3 digits for a branch.</summary>
    [GeneratedRegex(@"^\d{10}(-\d{3})?$")]
    private static partial Regex OrganizationTaxCode();

    /// <summary>A personal tax number (10 digits) or a personal ID number (12 digits), which is the personal tax number since 1/7/2025.</summary>
    [GeneratedRegex(@"^(\d{10}|\d{12})$")]
    private static partial Regex PersonalTaxNumber();
}
