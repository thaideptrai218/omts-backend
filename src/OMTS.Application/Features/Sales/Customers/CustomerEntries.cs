using System.Text.RegularExpressions;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The entries of a customer form, checked (SALE-04 step 6). Empty optional entries are null.</summary>
/// <param name="PhoneNumber">Normalized; null when the number is locked and so not read from the request.</param>
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
    string? CompanyName,
    string? TaxCode,
    string? IdentityNumber,
    string? BankBin,
    string? BankName,
    string? BankAccountNumber)
{
    private const int MaxAddressLength = 500;
    private const int MaxNoteLength = 1000;
    private const int MaxShortLength = 100;
    private const int MaxFacebookLength = 500;

    public static (CustomerEntries Entries, List<string> Invalid) From(SaveCustomerRequest request, bool phoneLocked, DateOnly today)
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

        var address = Optional(request.Address, MaxAddressLength, "Address", invalid);
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
        var province = Optional(request.Province, MaxShortLength, "Province", invalid);
        var ward = Optional(request.Ward, MaxShortLength, "Ward", invalid);
        var company = Optional(request.CompanyName, DisplayNames.MaxLength, "Company name", invalid);

        var taxCode = request.TaxCode?.Trim();
        if (!string.IsNullOrEmpty(taxCode) && !TaxCodeFormat().IsMatch(taxCode))
        {
            invalid.Add("Tax code");
        }

        var identity = request.IdentityNumber?.Trim();
        if (!string.IsNullOrEmpty(identity) && !IdentityNumberFormat().IsMatch(identity))
        {
            invalid.Add("Identity number");
        }

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
        return (new CustomerEntries(
            name, phone, address, note, request.CustomerType ?? Domain.Enums.CustomerType.Individual, request.Gender, request.BirthDate, phone2, email, facebook,
            province, ward, company, string.IsNullOrEmpty(taxCode) ? null : taxCode, string.IsNullOrEmpty(identity) ? null : identity,
            hasBank ? bin : null, hasBank ? bankName : null, hasBank ? number : null), invalid);
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

    [GeneratedRegex(@"^(\d{10}(-\d{3})?|\d{12})$")]
    private static partial Regex TaxCodeFormat();

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex IdentityNumberFormat();
}
