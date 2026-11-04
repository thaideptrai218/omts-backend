namespace OMTS.Application.Common;

/// <summary>
/// Masks contact data for logs and messages (MSG-ACCOUNT-19 "n***@mail.com"): enough to recognise the target,
/// not enough to read it.
/// </summary>
public static class Masking
{
    /// <summary>"0912345678" → "091****678".</summary>
    public static string Phone(string? phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber))
        {
            return string.Empty;
        }

        return phoneNumber.Length <= 6
            ? new string('*', phoneNumber.Length)
            : $"{phoneNumber[..3]}{new string('*', phoneNumber.Length - 6)}{phoneNumber[^3..]}";
    }

    /// <summary>A bank account number of an employee (6 digits or more), for audit rows and logs: "999900000001" → "********0001".</summary>
    public static string AccountNumber(string number) =>
        number.Length <= 4 ? number : new string('*', number.Length - 4) + number[^4..];

    /// <summary>The receiving bank account, which may be as short as 1 character: too short to show its ending, it shows nothing.</summary>
    public static string ReceivingAccountNumber(string number) =>
        number.Length <= 4 ? new string('*', number.Length) : AccountNumber(number);

    /// <summary>"nguyen@mail.com" → "n***@mail.com".</summary>
    public static string Email(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return string.Empty;
        }

        var at = email.IndexOf('@');
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
