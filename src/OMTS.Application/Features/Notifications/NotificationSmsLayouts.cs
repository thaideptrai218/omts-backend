using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Notifications;

/// <summary>A wording of a message over one SMS, given its free text in the order of <see cref="NotificationSmsLayout.FreeText"/>.</summary>
internal delegate string NotificationSmsForm(IReadOnlyList<string> freeText);

/// <summary>A field typed by a user, and the length it may shrink to before the wording around it gives way.</summary>
internal sealed record SmsFreeText(string Text, int Minimum);

/// <param name="FreeText">The free text of the message, in the order it gives way.</param>
/// <param name="Forms">The wordings, the fullest first. Each one holds every value, range, time and expiry date.</param>
internal sealed record NotificationSmsLayout(IReadOnlyList<SmsFreeText> FreeText, IReadOnlyList<NotificationSmsForm> Forms);

/// <summary>
/// How the messages that go out as an SMS shorten at 160 characters (<see cref="NotificationSmsText"/>). A layout reads
/// the parameters of the message, as any client that renders it does, and uses the builders of <see cref="UserMessages"/>
/// for the full wording. A message without a layout, like MSG-WATER-04 until it has a sender here, is cut at the end.
/// </summary>
internal static class NotificationSmsLayouts
{
    private const int ZoneMinimum = 12;
    private const int RaftMinimum = 6;
    private const int LicenseTypeMinimum = 12;
    private const int LicenseNumberMinimum = 20;
    private const int CertificationNameMinimum = 12;
    private const int NoteMinimum = 12;
    private const int CustomerMinimum = 12;
    private const int ProductMinimum = 12;

    private const string CheckTheWater = " Please check the water.";
    private const string RenewIt = " Please renew it and record the new one.";
    private const string AskTheZoneManager = " Please ask the Zone Manager to check the oysters, then respond.";

    public static NotificationSmsLayout? Of(UserMessage message) => (message.Code, message.Parameters) switch
    {
        (MessageCodes.Water03, { } parameters) => WaterAlert(parameters),
        (MessageCodes.Farm01 or MessageCodes.Farm02, { } parameters) => License(parameters) ?? Certification(parameters),
        (MessageCodes.Sale01, { } parameters) => PurchaseRequestNew(parameters),
        _ => null,
    };

    // The note gives way first, then the customer, then the product; the cages and the pickup date are no free text and stay.
    private static NotificationSmsLayout? PurchaseRequestNew(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.GetValueOrDefault("customer_name") is not string customer
            || parameters.GetValueOrDefault("requested_cages") is not int cages
            || parameters.GetValueOrDefault("product_name") is not string product
            || parameters.GetValueOrDefault("pickup_date") is not DateOnly pickup)
        {
            return null;
        }

        var note = parameters.GetValueOrDefault("note") as string;

        string Full(IReadOnlyList<string> free) => UserMessages.PurchaseRequestNew(free[^2], cages, free[^1], pickup, note is null ? null : free[0]).Text;

        return new NotificationSmsLayout(
            note is null
                ? [new SmsFreeText(customer, CustomerMinimum), new SmsFreeText(product, ProductMinimum)]
                : [new SmsFreeText(note, NoteMinimum), new SmsFreeText(customer, CustomerMinimum), new SmsFreeText(product, ProductMinimum)],
            [Full, free => WithoutEnding(Full(free), AskTheZoneManager)]);
    }

    private static NotificationSmsLayout? WaterAlert(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.GetValueOrDefault("zone_name") is not string zone
            || parameters.GetValueOrDefault("measurement_time") is not DateTimeOffset measuredAt
            || parameters.GetValueOrDefault("raft_code") is not string raft
            || parameters.GetValueOrDefault("values_outside") is not IReadOnlyList<WaterValueOutside> values)
        {
            return null;
        }

        var corrected = parameters.GetValueOrDefault("corrected") is true;

        string Full(IReadOnlyList<string> free) => UserMessages.WaterAlert(corrected, free[0], free[1], measuredAt, values).Text;
        string Short(IReadOnlyList<string> free) =>
            $"Water alert{(corrected ? " (corrected)" : "")}: zone {free[0]}, raft {free[1]}, at {BusinessClock.Format(measuredAt)}. "
            + $"{string.Join("; ", values.Select(v => v.Describe().Replace(" is too ", " ")))}.";

        return new NotificationSmsLayout(
            [new SmsFreeText(zone, ZoneMinimum), new SmsFreeText(raft, RaftMinimum)],
            [Full, free => WithoutEnding(Full(free), CheckTheWater), Short]);
    }

    private static NotificationSmsLayout? Certification(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.GetValueOrDefault("certification_name") is not string name
            || parameters.GetValueOrDefault("expiry_date") is not DateOnly expiry)
        {
            return null;
        }

        var number = parameters.GetValueOrDefault("certification_number") as string;
        var days = parameters.GetValueOrDefault("days") as int?;

        string Full(IReadOnlyList<string> free)
        {
            var shownNumber = number is null ? null : free[1];
            return (days is { } left
                ? UserMessages.CertificationExpiringSoon(free[0], shownNumber, expiry, left)
                : UserMessages.CertificationExpired(free[0], shownNumber, expiry)).Text;
        }

        return new NotificationSmsLayout(
            number is null
                ? [new SmsFreeText(name, CertificationNameMinimum)]
                : [new SmsFreeText(name, CertificationNameMinimum), new SmsFreeText(number, LicenseNumberMinimum)],
            [Full, free => WithoutEnding(Full(free), RenewIt)]);
    }

    private static NotificationSmsLayout? License(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.GetValueOrDefault("document_type") is not string type
            || parameters.GetValueOrDefault("document_number") is not string number
            || parameters.GetValueOrDefault("zone_codes") is not IReadOnlyList<string> zones
            || parameters.GetValueOrDefault("expiry_date") is not DateOnly expiry)
        {
            return null;
        }

        // MSG-FARM-01 has the days left; MSG-FARM-02 has none.
        var days = parameters.GetValueOrDefault("days") as int?;

        string Full(IReadOnlyList<string> free) => (days is { } left
            ? UserMessages.LicenseExpiringSoon(type, free[1], free[2], free[0], expiry, left)
            : UserMessages.LicenseExpired(type, free[1], free[2], free[0], expiry)).Text;

        return new NotificationSmsLayout(
            [
                new SmsFreeText(string.Join(", ", zones), ZoneMinimum),
                new SmsFreeText(LicenseTypes.Names.GetValueOrDefault(type, type), LicenseTypeMinimum),
                new SmsFreeText(number, LicenseNumberMinimum),
            ],
            [Full, free => WithoutEnding(Full(free), RenewIt)]);
    }

    private static string WithoutEnding(string text, string ending) => text.EndsWith(ending, StringComparison.Ordinal) ? text[..^ending.Length] : text;
}
