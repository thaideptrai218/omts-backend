using System.Text.RegularExpressions;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Employees.Dtos;

/// <summary>Validated, normalised employee profile fields (LAB-01 data rules).</summary>
public sealed partial record EmployeeInput(
    string FullName,
    string PhoneNumber,
    DateOnly? Birthday,
    string? Address,
    string? Area,
    string? Ward,
    string? OtherInformation,
    string? Notes,
    IReadOnlyList<Guid> WorkingZoneIds,
    BankAccountInfo? BankAccount)
{
    /// <summary>The normalised input, or the names of every invalid field.</summary>
    public static (EmployeeInput? Input, List<string> Invalid) From(SaveEmployeeRequest request, DateOnly today)
    {
        var invalid = new List<string>();
        var fullName = request.FullName?.Trim() ?? string.Empty;
        if (fullName.Length is 0 or > 100)
        {
            invalid.Add("Full name");
        }

        var phone = PhoneNumbers.Normalize(request.PhoneNumber);
        if (phone is null)
        {
            invalid.Add("Phone number");
        }

        if (request.Birthday > today)
        {
            invalid.Add("Birthday");
        }

        var address = Optional(request.Address, 500, "Address", invalid);
        var area = Optional(request.Area, 100, "Area", invalid);
        var ward = Optional(request.Ward, 100, "Ward", invalid);
        var other = Optional(request.OtherInformation, 1000, "Other information", invalid);
        var notes = Optional(request.Notes, 1000, "Notes", invalid);

        var zones = request.WorkingZoneIds ?? [];
        if (zones.Distinct().Count() != zones.Count)
        {
            invalid.Add("Working zones");
        }

        var bank = Bank(request.BankAccount, invalid);
        return invalid.Count > 0
            ? (null, invalid)
            : (new EmployeeInput(fullName, phone!, request.Birthday, address, area, ward, other, notes, zones, bank), invalid);
    }

    /// <summary>"0123456789" → "******6789": bank numbers appear masked in the audit log.</summary>
    public static string MaskAccountNumber(string number) =>
        number.Length <= 4 ? number : new string('*', number.Length - 4) + number[^4..];

    private static string? Optional(string? value, int max, string field, List<string> invalid)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed?.Length > max)
        {
            invalid.Add(field);
        }

        return trimmed;
    }

    private static BankAccountInfo? Bank(EmployeeBankAccountInput? input, List<string> invalid)
    {
        var name = input?.BankName?.Trim();
        var number = input?.AccountNumber?.Trim();
        var holder = input?.AccountHolderName?.Trim();
        var filled = new[] { name, number, holder }.Count(v => !string.IsNullOrEmpty(v));
        if (filled == 0)
        {
            return null;
        }

        if (filled < 3 || name!.Length > 100 || holder!.Length > 100 || !AccountNumberPattern().IsMatch(number!))
        {
            invalid.Add("Bank account");
            return null;
        }

        return new BankAccountInfo { BankName = name, AccountNumber = number!, AccountHolderName = holder.ToUpperInvariant() };
    }

    [GeneratedRegex(@"\A[0-9]{6,20}\z")]
    private static partial Regex AccountNumberPattern();
}
