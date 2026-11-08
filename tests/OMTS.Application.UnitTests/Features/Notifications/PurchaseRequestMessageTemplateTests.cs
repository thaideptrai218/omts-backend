using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// MSG-SALE-01 to MSG-SALE-03 fill the templates of SRS 27/10 Part V, copied here as data, and their parameters are atomic
/// values, so a client can show the text in its own language.
/// </summary>
public sealed partial class PurchaseRequestMessageTemplateTests
{
    private const string Sale01 = "{customer_name} asks to buy {requested_cages} cages of {product_name}, pickup on {pickup_date}. {note} Please ask the Zone Manager to check the oysters, then respond.";
    private const string Sale02 = "{customer_name} cancelled the purchase request of {request_date} for {requested_cages} cages of {product_name}. {reason}";
    private const string Sale03 = "{cooperative_short_name}: your request for {requested_cages} cages of {product_name} is {response_text}. {note}";

    public static TheoryData<string> Cases => new() { "new", "new-no-note", "cancelled", "cancelled-no-reason", "accepted", "accepted-pay-later", "declined" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_text_fills_the_srs_template_and_the_parameters_are_atomic(string name)
    {
        var (code, template, message, text, parameters) = Case(name);

        Assert.Equal(code, message.Code);
        Assert.Matches(Pattern(template), message.Text);
        Assert.Equal(text, message.Text);
        Assert.Equal(parameters, JsonDocument.Parse(MessageParameterJson.Serialize(message.Parameters)!).RootElement.GetRawText());
    }

    private static (string Code, string Template, UserMessage Message, string Text, string Parameters) Case(string name) => name switch
    {
        "new" => (
            "MSG-SALE-01", Sale01,
            UserMessages.PurchaseRequestNew("Chị Lan", 30, "Hàu sữa", new DateOnly(2026, 11, 7), "Lấy sớm"),
            "Chị Lan asks to buy 30 cages of Hàu sữa, pickup on 07/11/2026. Lấy sớm Please ask the Zone Manager to check the oysters, then respond.",
            """{"customer_name":"Chị Lan","requested_cages":30,"product_name":"Hàu sữa","pickup_date":"2026-11-07","note":"Lấy sớm"}"""),
        "new-no-note" => (
            "MSG-SALE-01", Sale01,
            UserMessages.PurchaseRequestNew("Chị Lan", 30, "Hàu sữa", new DateOnly(2026, 11, 7), null),
            "Chị Lan asks to buy 30 cages of Hàu sữa, pickup on 07/11/2026. Please ask the Zone Manager to check the oysters, then respond.",
            """{"customer_name":"Chị Lan","requested_cages":30,"product_name":"Hàu sữa","pickup_date":"2026-11-07","note":null}"""),
        "cancelled" => (
            "MSG-SALE-02", Sale02,
            UserMessages.PurchaseRequestCancelled("Chị Lan", new DateOnly(2026, 11, 5), 2, "Hàu sữa", "Đổi lịch"),
            "Chị Lan cancelled the purchase request of 05/11/2026 for 2 cages of Hàu sữa. Đổi lịch",
            """{"customer_name":"Chị Lan","request_date":"2026-11-05","requested_cages":2,"product_name":"Hàu sữa","reason":"Đổi lịch"}"""),
        "cancelled-no-reason" => (
            "MSG-SALE-02", Sale02,
            UserMessages.PurchaseRequestCancelled("Chị Lan", new DateOnly(2026, 11, 5), 10, "Hàu sữa", null),
            "Chị Lan cancelled the purchase request of 05/11/2026 for 10 cages of Hàu sữa.",
            """{"customer_name":"Chị Lan","request_date":"2026-11-05","requested_cages":10,"product_name":"Hàu sữa","reason":null}"""),
        "accepted" => (
            "MSG-SALE-03", Sale03,
            UserMessages.PurchaseRequestAccepted("HTX", 30, "Hàu sữa", 25, new DateOnly(2026, 11, 8), false, null),
            "HTX: your request for 30 cages of Hàu sữa is accepted for 25 cages, pickup on 08/11/2026.",
            """{"cooperative_short_name":"HTX","requested_cages":30,"product_name":"Hàu sữa","accepted":true,"accepted_cages":25,"pickup_date":"2026-11-08","pay_later":false,"note":null}"""),
        "accepted-pay-later" => (
            "MSG-SALE-03", Sale03,
            UserMessages.PurchaseRequestAccepted("HTX", 30, "Hàu sữa", 25, new DateOnly(2026, 11, 8), true, "Trả sau"),
            "HTX: your request for 30 cages of Hàu sữa is accepted for 25 cages, pickup on 08/11/2026; you may pay later. Trả sau",
            """{"cooperative_short_name":"HTX","requested_cages":30,"product_name":"Hàu sữa","accepted":true,"accepted_cages":25,"pickup_date":"2026-11-08","pay_later":true,"note":"Trả sau"}"""),
        "declined" => (
            "MSG-SALE-03", Sale03,
            UserMessages.PurchaseRequestDeclined("HTX", 30, "Hàu sữa", "Chưa đủ hàu"),
            "HTX: your request for 30 cages of Hàu sữa is declined. Chưa đủ hàu",
            """{"cooperative_short_name":"HTX","requested_cages":30,"product_name":"Hàu sữa","accepted":false,"note":"Chưa đủ hàu"}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // Each placeholder stands for some text; an empty {note} or {reason} also drops the space around it.
    private static string Pattern(string template)
    {
        var escaped = Regex.Escape(template)
            .Replace(@"\ \{note}\ ", @"( .+ | )", StringComparison.Ordinal)
            .Replace(@"\ \{note}", "( .+)?", StringComparison.Ordinal)
            .Replace(@"\ \{reason}", "( .+)?", StringComparison.Ordinal);
        return "^" + Placeholder().Replace(escaped, ".+") + "$";
    }

    [GeneratedRegex(@"\\\{[a-z_]+}")]
    private static partial Regex Placeholder();
}
