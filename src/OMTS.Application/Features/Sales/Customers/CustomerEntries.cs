using OMTS.Application.Common.Validation;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>The entries of a customer form, checked (SALE-04 step 6). Empty optional entries are null.</summary>
/// <param name="PhoneNumber">Normalized; null when the number is locked and so not read from the request.</param>
internal sealed record CustomerEntries(string Name, string? PhoneNumber, string? Address, string? Note)
{
    private const int MaxNameLength = 200;
    private const int MaxAddressLength = 500;
    private const int MaxNoteLength = 1000;

    public static (CustomerEntries Entries, List<string> Invalid) From(SaveCustomerRequest request, bool phoneLocked)
    {
        var invalid = new List<string>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            invalid.Add("Name");
        }

        var phone = phoneLocked ? null : PhoneNumbers.Normalize(request.PhoneNumber);
        if (!phoneLocked && phone is null)
        {
            invalid.Add("Phone number");
        }

        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        if (address?.Length > MaxAddressLength)
        {
            invalid.Add("Address");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        return (new CustomerEntries(name, phone, address, note), invalid);
    }
}
