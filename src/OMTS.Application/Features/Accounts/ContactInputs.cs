using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>Normalises a phone number or email typed by a user, for the channel it will be verified on.</summary>
public static class ContactInputs
{
    /// <summary>The normalised value, or null when it has the wrong format for the channel.</summary>
    public static string? Normalize(VerificationChannel channel, string? input) =>
        channel == VerificationChannel.Sms ? PhoneNumbers.Normalize(input) : EmailAddresses.Normalize(input);

    /// <summary>Field name used in MSG01 for the channel.</summary>
    public static string FieldName(VerificationChannel channel) => channel == VerificationChannel.Sms ? "Phone number" : "Email";

    /// <summary>"phone number" / "email", as used in MSG62.</summary>
    public static string ContactName(VerificationChannel channel) => channel == VerificationChannel.Sms ? "phone number" : "email";
}
