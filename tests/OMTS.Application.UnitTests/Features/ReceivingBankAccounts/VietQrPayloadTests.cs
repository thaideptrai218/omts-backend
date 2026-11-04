using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Application.UnitTests.Features.ReceivingBankAccounts;

/// <summary>
/// The VietQR text. Where each expected value comes from:
/// <list type="bullet">
/// <item>"123456789" → 0x29B1: the published check value of CRC-16/CCITT-FALSE.</item>
/// <item><see cref="PublishedExample"/>: a published example (viblo.asia, "Tự viết bộ tạo và đọc mã VietQR"), its CRC 79AB
/// recomputed with a separate script that does not use this code. Its account 0123456789 is a placeholder.</item>
/// <item>The other full strings: assembled by hand and their CRC computed with that same separate script, so they pin the
/// code against an independent calculation, not against itself.</item>
/// </list>
/// </summary>
public sealed class VietQrPayloadTests
{
    private const string PublishedExample =
        "00020101021238540010A00000072701240006970423011001234567890208QRIBFTTA530370454061500005802VN62120808DH123456630479AB";

    [Fact]
    public void The_crc_gives_the_published_check_value() => Assert.Equal(0x29B1, EmvQrEncoding.Crc16Ccitt("123456789"));

    [Fact]
    public void The_crc_of_nothing_is_its_initial_value() => Assert.Equal(0xFFFF, EmvQrEncoding.Crc16Ccitt(string.Empty));

    [Fact]
    public void The_published_example_is_built_to_the_letter() =>
        Assert.Equal(PublishedExample, VietQrPayload.Build("970423", "0123456789", 150000, "DH123456"));

    [Theory]
    [InlineData("970436", "0000000000", 1L, "HD000001", "00020101021238540010A00000072701240006970436011000000000000208QRIBFTTA5303704540115802VN62120808HD0000016304F668")]
    [InlineData("970418", "TEST0000000000000AB", 12000000L, "HD000123", "00020101021238630010A000000727013300069704180119TEST0000000000000AB0208QRIBFTTA53037045408120000005802VN62120808HD0001236304DACF")]
    [InlineData("970436", "DEMO00000001", 12000000L, "Hàu Vân Đồn", "00020101021238560010A000000727012600069704360112DEMO000000010208QRIBFTTA53037045408120000005802VN62150811Hau Van Don63042E74")]
    public void Independently_computed_vectors_match(string bin, string account, long amount, string content, string expected) =>
        Assert.Equal(expected, VietQrPayload.Build(bin, account, amount, content));

    [Fact]
    public void Fields_come_in_the_order_and_with_the_lengths_of_the_standard()
    {
        var fields = Read(VietQrPayload.Build("970418", "TEST00112233", 12000000, "HD000123"));

        Assert.Equal(["00", "01", "38", "53", "54", "58", "62", "63"], fields.Select(f => f.Id));
        Assert.Equal("01", Value(fields, "00"));
        Assert.Equal("12", Value(fields, "01"));
        Assert.Equal("704", Value(fields, "53"));
        Assert.Equal("12000000", Value(fields, "54"));
        Assert.Equal("VN", Value(fields, "58"));

        var merchant = Read(Value(fields, "38"));
        Assert.Equal(["00", "01", "02"], merchant.Select(f => f.Id));
        Assert.Equal("A000000727", Value(merchant, "00"));
        Assert.Equal("QRIBFTTA", Value(merchant, "02"));

        var beneficiary = Read(Value(merchant, "01"));
        Assert.Equal(["00", "01"], beneficiary.Select(f => f.Id));
        Assert.Equal("970418", Value(beneficiary, "00"));
        Assert.Equal("TEST00112233", Value(beneficiary, "01"));

        var additional = Read(Value(fields, "62"));
        Assert.Equal("HD000123", Assert.Single(additional).Value);
    }

    [Fact]
    public void The_crc_field_closes_the_text_and_covers_everything_before_its_value()
    {
        var payload = VietQrPayload.Build("970418", "TEST00112233", 12000000, "HD000123");

        Assert.Equal("6304", payload[^8..^4]);
        Assert.Equal(EmvQrEncoding.Crc16Ccitt(payload[..^4]).ToString("X4"), payload[^4..]);
    }

    [Fact]
    public void A_change_of_the_amount_or_the_account_changes_the_crc()
    {
        var crc = (string payload) => payload[^4..];
        var original = VietQrPayload.Build("970418", "TEST00112233", 12000000, "HD000123");

        Assert.NotEqual(crc(original), crc(VietQrPayload.Build("970418", "TEST00112234", 12000000, "HD000123")));
        Assert.NotEqual(crc(original), crc(VietQrPayload.Build("970418", "TEST00112233", 12000001, "HD000123")));
    }

    [Theory]
    [InlineData("Hàu Vân Đồn", "Hau Van Don")]
    [InlineData("thanh toán đơn hàng", "thanh toan don hang")]
    [InlineData("Nguyễn Thị Ưng Ổi", "Nguyen Thi Ung Oi")]
    [InlineData("  HD000123 \t chuyển\nkhoản  ", "HD000123 chuyen khoan")]
    [InlineData("HD000123 🦪", "HD000123")]
    public void Vietnamese_content_becomes_plain_ascii(string content, string expected) =>
        Assert.Equal(expected, VietQrPayload.ContentOf(content));

    [Fact]
    public void Vietnamese_content_keeps_the_lengths_right_in_the_payload()
    {
        var fields = Read(VietQrPayload.Build("970418", "TEST00112233", 1000, "Hàu Vân Đồn"));

        Assert.Equal("Hau Van Don", Assert.Single(Read(Value(fields, "62"))).Value);
        Assert.All(VietQrPayload.Build("970418", "TEST00112233", 1000, "Hàu Vân Đồn"), letter => Assert.True(letter < 128));
    }

    [Fact]
    public void Long_content_is_cut_to_25_characters_and_the_invoice_code_stays()
    {
        var content = "HD000123 Hợp tác xã nuôi hàu Thái Bình Dương Vân Đồn Quảng Ninh";

        var clean = VietQrPayload.ContentOf(content);
        var fields = Read(VietQrPayload.Build("970418", "TEST00112233", 1000, content));

        Assert.StartsWith("HD000123 ", clean);
        Assert.True(clean.Length <= VietQrPayload.MaxContentLength, clean);
        Assert.Equal(clean, Assert.Single(Read(Value(fields, "62"))).Value);
    }

    [Fact]
    public void The_longest_values_still_fit_the_two_digit_lengths()
    {
        var fields = Read(VietQrPayload.Build("970418", new string('9', 19), 9_999_999_999_999, new string('X', 40)));

        Assert.Equal(13, Value(fields, "54").Length);
        Assert.Equal(new string('X', 25), Assert.Single(Read(Value(fields, "62"))).Value);
        Assert.Equal(new string('9', 19), Value(Read(Value(Read(Value(fields, "38")), "01")), "01"));
    }

    [Theory]
    [InlineData("97041", "TEST00112233", 1000L, "HD000123")]
    [InlineData("9704188", "TEST00112233", 1000L, "HD000123")]
    [InlineData("97041A", "TEST00112233", 1000L, "HD000123")]
    [InlineData("970418", "", 1000L, "HD000123")]
    [InlineData("970418", "TEST 00112233", 1000L, "HD000123")]
    [InlineData("970418", "12345678901234567890", 1000L, "HD000123")]
    [InlineData("970418", "TÀI00112233", 1000L, "HD000123")]
    [InlineData("970418", "TEST00112233", 0L, "HD000123")]
    [InlineData("970418", "TEST00112233", -1L, "HD000123")]
    [InlineData("970418", "TEST00112233", 10_000_000_000_000L, "HD000123")]
    [InlineData("970418", "TEST00112233", 1000L, "")]
    [InlineData("970418", "TEST00112233", 1000L, "🦪")]
    public void A_value_that_cannot_be_in_a_code_is_refused(string bin, string account, long amount, string content) =>
        Assert.ThrowsAny<ArgumentException>(() => VietQrPayload.Build(bin, account, amount, content));

    [Fact]
    public void A_field_refuses_a_value_that_is_not_ascii_or_too_long()
    {
        Assert.Throws<ArgumentException>(() => EmvQrEncoding.Field("62", "Vân"));
        Assert.Throws<ArgumentException>(() => EmvQrEncoding.Field("62", new string('X', 100)));
        Assert.Equal("6299" + new string('X', 99), EmvQrEncoding.Field("62", new string('X', 99)));
    }

    /// <summary>A separate reader of the format: id, two-digit length, value; the text must end exactly on a field.</summary>
    private static List<(string Id, string Value)> Read(string text)
    {
        var fields = new List<(string, string)>();
        for (var at = 0; at < text.Length;)
        {
            var length = int.Parse(text.Substring(at + 2, 2));
            fields.Add((text.Substring(at, 2), text.Substring(at + 4, length)));
            at += 4 + length;
        }

        return fields;
    }

    private static string Value(List<(string Id, string Value)> fields, string id) => fields.Single(f => f.Id == id).Value;
}
