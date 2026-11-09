using System.Globalization;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>QA of the texts of MSG-CLAIM-01, 02, 03 and of the extraction of <see cref="TraderMessageText"/> (OMTS-225).</summary>
public class QualityClaimMessagesQaTests
{
    private const int MaxNoteInSms = 80;

    // The cut of a purchase request note before the extraction (PurchaseRequestNotifications.ForSms, 34e686b1), kept here to prove the shared one reads the same.
    private static string? OldForSms(string? note)
    {
        if (note is not { Length: > MaxNoteInSms })
        {
            return note;
        }

        var end = 0;
        for (var next = 0; next <= MaxNoteInSms; next += StringInfo.GetNextTextElementLength(note.AsSpan(next)))
        {
            end = next;
        }

        return note[..end].TrimEnd() + "...";
    }

    public static TheoryData<string?> Notes() => new()
    {
        null,
        "",
        "short",
        new string('a', 80),
        new string('a', 81),
        new string('a', 79) + " b" + new string('c', 40),
        new string('a', 79) + "\U0001F41A" + "b",
        new string('a', 79) + "é" + "b",
        new string('a', 78) + "\U0001F469‍\U0001F469‍\U0001F467" + new string('b', 20),
        new string(' ', 90),
        new string('n', 1000),
    };

    [Theory]
    [MemberData(nameof(Notes))]
    public void The_shared_cut_reads_as_the_cut_of_a_purchase_request_note_did(string? note) =>
        Assert.Equal(OldForSms(note), TraderMessageText.Shorten(note, MaxNoteInSms));

    [Fact]
    public void A_random_note_is_cut_the_same_at_every_length_around_the_limit()
    {
        var random = new Random(225);
        var alphabet = "aăâbcdêeghiklmnôơopqrstuưvxy ".ToCharArray();
        for (var round = 0; round < 500; round++)
        {
            var length = random.Next(70, 95);
            var note = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            Assert.Equal(OldForSms(note), TraderMessageText.Shorten(note, MaxNoteInSms));
        }
    }

    [Fact]
    public void Msg_claim_01_has_the_srs_text_and_parameters()
    {
        var message = UserMessages.QualityClaimNew("DEMO Trader", "HD000001", "Dead oysters");

        Assert.Equal("MSG-CLAIM-01", message.Code);
        Assert.Equal("DEMO Trader reported a quality problem with invoice HD000001: Dead oysters. Please ask the Zone Manager to check it, then respond.", message.Text);
        Assert.Equal(new[] { "customer_name", "invoice_code", "description_start" }, message.Parameters!.Keys);
    }

    [Theory]
    [InlineData(true, "ReturnAllGoods", "Sorry", "DEMO: your quality claim on invoice HD1 is accepted: the cooperative offers a return of all the goods. Sorry Please call 999900000001.")]
    [InlineData(true, "PriceCutWholeInvoice", null, "DEMO: your quality claim on invoice HD1 is accepted: the cooperative offers a price cut of the whole invoice. Please call 999900000001.")]
    [InlineData(false, null, "Too late", "DEMO: your quality claim on invoice HD1 is not accepted. Too late Please call 999900000001.")]
    public void Msg_claim_02_follows_the_srs_template(bool accepted, string? offer, string? note, string expected)
    {
        var message = UserMessages.QualityClaimResponse("DEMO", "HD1", accepted, offer, note, "999900000001");

        Assert.Equal("MSG-CLAIM-02", message.Code);
        Assert.Equal(expected, message.Text);
    }

    [Fact]
    public void Msg_claim_02_without_a_cooperative_phone_still_reads()
    {
        var message = UserMessages.QualityClaimResponse("DEMO", "HD1", false, null, "No", null);

        Assert.Equal("DEMO: your quality claim on invoice HD1 is not accepted. No Please call the cooperative.", message.Text);
    }

    [Theory]
    [InlineData("was cancelled")]
    [InlineData("has a sales return")]
    public void Msg_claim_03_has_the_srs_text(string cause)
    {
        var message = UserMessages.QualityClaimCannotBeAccepted("HD1", cause);

        Assert.Equal("MSG-CLAIM-03", message.Code);
        Assert.Equal($"Invoice HD1 {cause}, so the claim cannot be accepted. You can still reject it with a note.", message.Text);
    }
}
