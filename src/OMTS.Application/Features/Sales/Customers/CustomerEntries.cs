using System.Text.RegularExpressions;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Parties;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The entries of a customer form, checked (SALE-04 step 6). Empty optional entries are null.</summary>
/// <param name="PhoneNumber">Normalized; null when the number is locked and so not read from the request.</param>
/// <param name="Address">The street of the contact address.</param>
/// <param name="Invoice">Who the e-invoice is made out to; its name is the <c>CompanyName</c> of the profile. The invoice address is checked against the administrative units list by <see cref="PartyAddressVerifier"/>.</param>
/// <param name="BankName">The short name of the bank of <paramref name="BankBin"/>.</param>
internal sealed partial record CustomerEntries(
    string Name,
    string? PhoneNumber,
    string? Address,
    string? Note,
    CustomerType CustomerType,
    Gender? Gender,
    DateOnly? BirthDate,
    string? PhoneNumber2,
    string? Email,
    string? Facebook,
    string? Province,
    string? Ward,
    string? BankBin,
    string? BankName,
    string? BankAccountNumber,
    int? ProvinceCode,
    int? WardCode,
    InvoiceInfo Invoice)
{
    private const int MaxNoteLength = 1000;
    private const int MaxFacebookLength = 500;

    /// <summary>The contact address as entered, for <see cref="PartyAddressVerifier"/>.</summary>
    public AddressParts ContactAddress => new(ProvinceCode, Province, WardCode, Ward, Address);

    public CustomerEntries WithContactAddress(AddressParts address) =>
        this with { Address = address.Street, Province = address.ProvinceName, ProvinceCode = address.ProvinceCode, Ward = address.WardName, WardCode = address.WardCode };

    /// <param name="required">The optional fields the cooperative makes required; null: none.</param>
    public static (CustomerEntries Entries, List<string> Invalid) From(SaveCustomerRequest request, bool phoneLocked, DateOnly today, CustomerRequiredFields? required = null)
    {
        var invalid = new List<string>();
        var name = DisplayNames.Normalize(request.Name);
        if (name.Length is 0 or > DisplayNames.MaxLength)
        {
            invalid.Add("Name");
        }

        var phone = phoneLocked ? null : PhoneNumbers.Normalize(request.PhoneNumber);
        if (!phoneLocked && phone is null)
        {
            invalid.Add("Phone number");
        }

        var contact = PartyInvoiceRules.Contact(
            request.ProvinceCode, PartyInvoiceRules.First(request.Province, request.ProvinceName), request.WardCode, PartyInvoiceRules.First(request.Ward, request.WardName), PartyInvoiceRules.First(request.Address, request.Street), invalid);
        var note = Optional(request.Note, MaxNoteLength, "Note", invalid);

        string? phone2 = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber2) && (phone2 = PhoneNumbers.Normalize(request.PhoneNumber2)) is null)
        {
            invalid.Add("Second phone number");
        }

        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email) && (email = EmailAddresses.Normalize(request.Email)) is null)
        {
            invalid.Add("Email");
        }

        if (request.CustomerType is { } type && !Enum.IsDefined(type))
        {
            invalid.Add("Customer type");
        }

        if (request.Gender is { } gender && !Enum.IsDefined(gender))
        {
            invalid.Add("Gender");
        }

        if (request.BirthDate is { } birth && (birth > today || birth.Year < 1900))
        {
            invalid.Add("Birth date");
        }

        var facebook = Optional(request.Facebook, MaxFacebookLength, "Facebook", invalid);

        // The company name of older clients is the invoice name.
        var invoice = PartyInvoiceRules.Invoice(
            new InvoiceInfoInput(
                request.InvoiceBuyerType, PartyInvoiceRules.First(request.InvoiceName, request.CompanyName), request.TaxCode, request.PersonalTaxId, request.InvoiceAddressSameAsContact,
                new AddressParts(request.InvoiceProvinceCode, request.InvoiceProvinceName, request.InvoiceWardCode, request.InvoiceWardName, request.InvoiceStreet),
                request.InvoiceEmail),
            contact, InvoiceBuyerType.Consumer, allowConsumer: true, invalid);

        // The bank and the number go together; the bank must be on the bank list, which also gives its name.
        var bin = request.BankBin?.Trim();
        var number = request.BankAccountNumber?.Trim();
        string? bankName = null;
        if (!string.IsNullOrEmpty(bin) || !string.IsNullOrEmpty(number))
        {
            if (string.IsNullOrEmpty(bin) || BankCatalog.FindByBin(bin) is not { } bank)
            {
                invalid.Add("Bank");
            }
            else
            {
                bankName = bank.ShortName;
            }

            if (string.IsNullOrEmpty(number) || !ReceivingBankAccountRules.IsValidAccountNumber(number))
            {
                invalid.Add("Bank account number");
            }
        }

        var hasBank = !string.IsNullOrEmpty(bin) && !string.IsNullOrEmpty(number);
        if (required is not null)
        {
            Require(required.PhoneNumber2, !string.IsNullOrWhiteSpace(request.PhoneNumber2), "Second phone number", invalid);
            Require(required.Email, !string.IsNullOrWhiteSpace(request.Email), "Email", invalid);
            Require(required.Gender, request.Gender is not null, "Gender", invalid);
            Require(required.BirthDate, request.BirthDate is not null, "Birth date", invalid);
            Require(required.Address, contact.Street is not null, "Address", invalid);
            Require(required.Address, contact.ProvinceName is not null, "Province", invalid);
            Require(required.Address, contact.WardName is not null, "Ward", invalid);
            Require(required.Facebook, facebook is not null, "Facebook", invalid);
            Require(required.Bank, hasBank, "Bank", invalid);
            Require(required.Bank, hasBank, "Bank account number", invalid);
            Require(required.TaxCode, !string.IsNullOrWhiteSpace(request.TaxCode) || !string.IsNullOrWhiteSpace(request.PersonalTaxId), "Tax code", invalid);
        }

        return (new CustomerEntries(
            name, phone, contact.Street, note, request.CustomerType ?? Domain.Enums.CustomerType.Individual, request.Gender, request.BirthDate, phone2, email, facebook,
            contact.ProvinceName, contact.WardName, hasBank ? bin : null, hasBank ? bankName : null, hasBank ? number : null, contact.ProvinceCode, contact.WardCode, invoice), invalid);
    }

    /// <summary>A required field that is missing is invalid, once: a field already invalid for another reason is not named twice.</summary>
    private static void Require(bool required, bool present, string field, List<string> invalid)
    {
        if (required && !present && !invalid.Contains(field))
        {
            invalid.Add(field);
        }
    }

    /// <summary>Trimmed; null when empty; its name is added to <paramref name="invalid"/> when it is longer than <paramref name="max"/>.</summary>
    private static string? Optional(string? value, int max, string field, List<string> invalid)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text?.Length > max)
        {
            invalid.Add(field);
        }

        return text;
    }

    /// <summary>The user-typed code of the request, validated by <see cref="DocumentCodes.TypedAllowed"/> (called apart: it needs the other profiles).</summary>
    public static bool CodeAllowed(string? code) => DocumentCodes.TypedAllowed(code);
}
