using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Farming;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>Checked FARM-01 profile fields; empty optional values are null.</summary>
internal sealed partial record CooperativeProfileInput(
    string FullName,
    string ShortName,
    string TaxCode,
    int? YearEstablished,
    string OfficeAddress,
    string PhoneNumber,
    string? Email,
    string? Website,
    string? SocialLink,
    string? IntroductionVi,
    string? IntroductionEn,
    string? LegalRepresentative,
    IReadOnlyList<string> BusinessLines)
{
    public const int MaxText = 255;
    public const int MaxShortName = 100;
    public const int MaxAddress = 500;
    public const int MaxIntroduction = 4000;
    public const int FirstYear = 1900;

    // Cooperatives.BusinessLines column length.
    private const int MaxBusinessLinesJson = 2000;

    // Stored text only (never written into HTML), so letters such as "ư" stay unescaped and fit the column.
    public static readonly JsonSerializerOptions LinesJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Mandatory: full name, short name, tax code, office address, phone number (FARM-01 step 3, short name for the SMS
    /// texts). Every invalid field is named, in form order (MSG-GEN-01).
    /// </summary>
    /// <param name="currentYear">The business year: the year established cannot be later.</param>
    public static (CooperativeProfileInput Input, IReadOnlyList<string> Invalid) Read(UpdateCooperativeProfileRequest request, int currentYear)
    {
        var check = new FieldCheck();
        var fullName = check.Required(request.FullName, "Full name", MaxText);
        var shortName = check.Required(request.ShortName, "Short name", MaxShortName);
        var taxCode = check.Required(request.TaxCode, "Tax code", 14);
        if (taxCode.Length > 0 && !TaxCodePattern().IsMatch(taxCode) && !check.Invalid.Contains("Tax code"))
        {
            check.Invalid.Add("Tax code");
        }

        check.Check(request.YearEstablished is not { } year || (year >= FirstYear && year <= currentYear), "Year established");
        var address = check.Required(request.OfficeAddress, "Office address", MaxAddress);
        var phone = PhoneNumbers.Normalize(request.PhoneNumber);
        check.Check(phone is not null, "Phone number");

        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            email = EmailAddresses.Normalize(request.Email);
            check.Check(email is { Length: <= MaxText }, "Email");
        }

        var website = Link(check, request.Website, "Website", "http", "https");
        var socialLink = Link(check, request.SocialLink, "Social link", "https");
        var introductionVi = check.Optional(request.IntroductionVi, "Introduction (Vietnamese)", MaxIntroduction);
        var introductionEn = check.Optional(request.IntroductionEn, "Introduction (English)", MaxIntroduction);
        var representative = check.Optional(request.LegalRepresentative, "Legal representative", MaxText);

        var lines = (request.BusinessLines ?? []).Select(l => l?.Trim() ?? string.Empty).ToList();
        check.Check(
            lines.All(l => l.Length is > 0 and <= MaxText) && JsonSerializer.Serialize(lines, LinesJson).Length <= MaxBusinessLinesJson,
            "Business lines");

        var input = new CooperativeProfileInput(
            fullName, shortName, taxCode, request.YearEstablished, address, phone ?? string.Empty, email, website, socialLink,
            introductionVi, introductionEn, representative, lines);
        return (input, check.Invalid);
    }

    /// <summary>An absolute URL with one of the schemes, at most 255 characters; blank is null.</summary>
    private static string? Link(FieldCheck check, string? value, string field, params string[] schemes)
    {
        var link = check.Optional(value, field, MaxText);
        if (link is not null && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !schemes.Contains(uri.Scheme)) && !check.Invalid.Contains(field))
        {
            check.Invalid.Add(field);
        }

        return link;
    }

    [GeneratedRegex(@"\A[0-9]{10}(-[0-9]{3})?\z")]
    private static partial Regex TaxCodePattern();
}
