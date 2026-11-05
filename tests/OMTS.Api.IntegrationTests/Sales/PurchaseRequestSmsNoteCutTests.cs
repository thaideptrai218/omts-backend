using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>MSG-SALE-03: the cut of a note at 80 falls between characters as the reader sees them, in any script.</summary>
public class PurchaseRequestSmsNoteCutTests(PurchaseRequestQaSendFactory factory) : IClassFixture<PurchaseRequestQaSendFactory>
{
    private async Task<string> SmsNoteAsync(string note)
    {
        factory.Sender.Send = _ => Task.FromResult(DeliveryResult.Sent("stub"));
        factory.Gate.FailSaveNumber = 0;
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());

        (await owner.DeclineAsync(request, note)).EnsureSuccessStatusCode();

        Notification sms = (await factory.NotificationRowsAsync(request.Id())).Sendings().Single();
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sms.MessageParams!)!["note"].GetString()!;
    }

    // What would straddle characters 80 and 81: the whole of it goes.
    [Theory]
    [InlineData("\U0001F600")] // An emoji, two chars.
    [InlineData("é")] // A letter and its combining mark.
    [InlineData("กิ")] // Thai: a consonant and its vowel above.
    [InlineData("कि")] // Devanagari: a consonant and its vowel sign.
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467")] // A family, joined.
    [InlineData("\U0001F1FB\U0001F1F3")] // A flag, two regional indicators.
    public async Task What_crosses_the_limit_is_left_out_whole(string character)
    {
        var note = new string('a', 79) + character + " cảm ơn anh";

        Assert.Equal(new string('a', 79) + "...", await SmsNoteAsync(note));
    }

    // Vietnamese typed in decomposed form: "ữ" is three chars, so 26 whole letters fit in 80 and the 27th is not broken.
    [Fact]
    public async Task Vietnamese_in_decomposed_form_keeps_each_letter_whole()
    {
        var letter = "ữ";

        Assert.Equal(string.Concat(Enumerable.Repeat(letter, 26)) + "...", await SmsNoteAsync(string.Concat(Enumerable.Repeat(letter, 40))));
    }

    // A space at the cut goes too, as before.
    [Fact]
    public async Task The_end_of_a_cut_note_is_trimmed()
    {
        Assert.Equal(new string('a', 70) + "...", await SmsNoteAsync(new string('a', 70) + new string(' ', 20) + "b"));
    }
}
