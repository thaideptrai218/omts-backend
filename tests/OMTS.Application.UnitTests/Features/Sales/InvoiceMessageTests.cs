using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>MSG-SALE-15/16/07 (SALE-06, SALE-05 step 9) and the stale Sending row (BR-SALE-09). The paid part of an invoice is BR-DEBT-05 (CustomerDebtAllocatorTests).</summary>
public class InvoiceMessageTests
{
    private static readonly DateOnly Day = new(2026, 11, 7);

    private static UserMessage Message(decimal paid, decimal toPay, string? account = "999900000001", string? link = null, bool corrected = false) =>
        UserMessages.InvoiceMessage("HTX Trung Nam", "HD000012", corrected, Day, 200.5m, 6_015_000m, paid, toPay, account, account is null ? null : "DEMOBANK", link);

    // BR-ORIGIN-01: the start-up check of Provenance:PublicBaseUrl accepts only an absolute http(s) URL, so no QR code or SMS gets a relative link.
    [Theory]
    [InlineData("https://web.example", true)]
    [InlineData("http://localhost:3000/", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/web", false)]
    [InlineData("localhost:3000", false)]
    [InlineData("ftp://web.example", false)]
    public void The_public_base_url_must_be_an_absolute_web_address(string value, bool accepted)
    {
        var options = new ProvenanceOptions { PublicBaseUrl = value };

        Assert.Equal(accepted, options.HasWebAddress());
        if (accepted)
        {
            Assert.StartsWith(value.TrimEnd('/') + "/origin/", options.LinkOf("token"));
        }
    }

    [Fact]
    public void A_paid_invoice_without_a_qr_code_names_neither_an_account_nor_a_link()
    {
        var message = Message(6_015_000m, 0m);

        Assert.Equal("MSG-SALE-15", message.Code);
        Assert.Equal("HTX Trung Nam: invoice HD000012 of 07/11/2026: 200.5 kg, 6,015,000 VND, paid 6,015,000 VND.", message.Text);
        Assert.False(message.Parameters!.ContainsKey(UserMessages.InvoiceMessageBankAccountParameter));
        Assert.Null(message.Parameters["provenance_link"]);
    }

    [Fact]
    public void The_part_to_pay_names_the_receiving_account_and_the_transfer_content_and_the_qr_code_adds_the_origin_link()
    {
        var message = Message(1_000_000m, 5_015_000m, link: "https://web.example/origin/abc");

        Assert.Equal(
            "HTX Trung Nam: invoice HD000012 of 07/11/2026: 200.5 kg, 6,015,000 VND, paid 1,000,000 VND; to pay 5,015,000 VND to account 999900000001 (DEMOBANK), transfer content HD000012. Origin: https://web.example/origin/abc",
            message.Text);
        var parameters = message.Parameters!;
        Assert.Equal<object?>("999900000001", parameters[UserMessages.InvoiceMessageBankAccountParameter]);
        Assert.Equal<object?>("DEMOBANK", parameters[UserMessages.InvoiceMessageBankNameParameter]);
        Assert.Equal<object?>(5_015_000m, parameters["to_pay_amount"]);
        Assert.Equal<object?>(200.5m, parameters["total_kg"]);
    }

    [Fact]
    public void Without_a_receiving_account_the_part_to_pay_is_only_its_amount_and_a_correction_is_marked()
    {
        var message = Message(0m, 6_015_000m, account: null, corrected: true);

        Assert.Equal("HTX Trung Nam: invoice HD000012 (corrected) of 07/11/2026: 200.5 kg, 6,015,000 VND, paid 0 VND; to pay 6,015,000 VND.", message.Text);
        Assert.False(message.Parameters!.ContainsKey(UserMessages.InvoiceMessageBankAccountParameter));
        Assert.Equal<object?>(true, message.Parameters["corrected"]);
    }

    [Fact]
    public void Msg_sale_16_names_the_cause_and_msg_sale_07_the_invoice()
    {
        Assert.Equal(("MSG-SALE-16", "Invoice HD000012 is not sent: it has no customer profile. Please show the invoice to the trader on your screen."),
            (UserMessages.InvoiceNotSent("HD000012", hasCustomerProfile: false).Code, UserMessages.InvoiceNotSent("HD000012", hasCustomerProfile: false).Text));
        Assert.Equal("Invoice HD000012 is not sent: the customer profile has no phone number. Please show the invoice to the trader on your screen.",
            UserMessages.InvoiceNotSent("HD000012", hasCustomerProfile: true).Text);
        Assert.Equal(("MSG-SALE-07", "Does the trader want the QR code of invoice HD000012, to see where the oysters come from?"),
            (UserMessages.WantsQrCode("HD000012").Code, UserMessages.WantsQrCode("HD000012").Text));
        Assert.Contains(MessageCodes.Sale15, MessageCodes.NewWithoutLegacyCode);
        Assert.Contains(MessageCodes.Sale16, MessageCodes.NewWithoutLegacyCode);
        Assert.Contains(MessageCodes.Sale07, MessageCodes.NewWithoutLegacyCode);
    }

    [Fact]
    public void A_sending_row_left_sending_past_any_send_reads_not_sent()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(StatusIds.MessageDelivery.Sending, TraderMessageService.EffectiveStatus(StatusIds.MessageDelivery.Sending, now.AddSeconds(-30), now));
        Assert.Equal(StatusIds.MessageDelivery.NotSent, TraderMessageService.EffectiveStatus(StatusIds.MessageDelivery.Sending, now - TraderMessageService.StaleSendingAfter - TimeSpan.FromSeconds(1), now));
        Assert.Equal(StatusIds.MessageDelivery.Sent, TraderMessageService.EffectiveStatus(StatusIds.MessageDelivery.Sent, now.AddDays(-3), now));
    }
}
