using System.Globalization;
using System.Text;
using OMTS.Application.Common;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Application.UnitTests.Features.ReceivingBankAccounts;

/// <summary>
/// QA of the VietQR text. Every full string below comes from a separate Python script with its own TLV assembly and a
/// table-driven CRC-16/CCITT-FALSE (checked against 0x29B1 and the published example); nothing is copied from this code.
/// </summary>
public sealed class VietQrPayloadQaTests
{
    private const string Account = "TESTQA000001";

    [Theory]
    [InlineData("970436", Account, 1L, "HD000001", "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA5303704540115802VN62120808HD0000016304AFAB")]
    [InlineData("970418", "t", 9_999_999_999_999L, "A", "00020101021238450010A000000727011500069704180101t0208QRIBFTTA5303704541399999999999995802VN62050801A63044F9D")]
    [InlineData("970418", "ZZZZZZZZZZZZZZZZZZZ", 9_999_999_999_999L, "XXXXXXXXXXXXXXXXXXXXXXXXX", "00020101021238630010A000000727013300069704180119ZZZZZZZZZZZZZZZZZZZ0208QRIBFTTA5303704541399999999999995802VN62290825XXXXXXXXXXXXXXXXXXXXXXXXX6304688F")]
    [InlineData("970436", Account, 12_000_000L, "Hàu Vân Đồn", "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA53037045408120000005802VN62150811Hau Van Don63044C31")]
    [InlineData("970436", Account, 150_000L, "HD000123 thanh toán đơn hàng", "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA530370454061500005802VN62290825HD000123 thanh toan don h63048333")]
    [InlineData("970436", Account, 150_000L, "6304ABCD", "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA530370454061500005802VN621208086304ABCD6304143E")]
    [InlineData("970436", Account, 150_000L, "a~b!c{d}e", "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA530370454061500005802VN62130809a~b!c{d}e63048409")]
    public void Independent_vectors_match(string bin, string account, long amount, string content, string expected) =>
        Assert.Equal(expected, VietQrPayload.Build(bin, account, amount, content));

    // A CRC below 0x1000 needs its leading zeros: amounts found by the script for one, two and three of them.
    [Theory]
    [InlineData(42L, "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA53037045402425802VN62120808HD00012363040118")]
    [InlineData(277L, "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA530370454032775802VN62120808HD000123630400B0")]
    [InlineData(1490L, "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA5303704540414905802VN62120808HD00012363040001")]
    public void The_crc_keeps_its_leading_zeros_and_capitals(long amount, string expected) =>
        Assert.Equal(expected, VietQrPayload.Build("970436", Account, amount, "HD000123"));

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("tr-TR")]
    [InlineData("vi-VN")]
    public void The_culture_of_the_server_changes_nothing(string culture)
    {
        var earlier = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(
                "00020101021238560010A000000727012600069704360112TESTQA0000010208QRIBFTTA53037045408120000005802VN62150811Hau Van Don63044C31",
                VietQrPayload.Build("970436", Account, 12_000_000, "Hàu Vân Đồn"));
        }
        finally
        {
            CultureInfo.CurrentCulture = earlier;
        }
    }

    // Length in characters equals length in bytes: the whole text is ASCII, whatever the content was.
    [Theory]
    [InlineData("Hàu Vân Đồn")]
    [InlineData("ẮẰẲẴẶấầẩẫậđĐơƠưƯ")]
    [InlineData("🦪 HD000123 🦪")]
    [InlineData("HD\x200B000\x202E123\xFEFF")]
    [InlineData("Ｆｕｌｌ ｗｉｄｔｈ HD000123")]
    public void Every_length_counts_bytes_and_characters_alike(string content)
    {
        var payload = VietQrPayload.Build("970436", Account, 1000, content);

        Assert.Equal(payload.Length, Encoding.UTF8.GetByteCount(payload));
        var fields = Read(payload);
        Assert.Equal(["00", "01", "38", "53", "54", "58", "62", "63"], fields.Select(f => f.Id));
        Assert.Equal(payload[^4..], Crc(payload[..^4]));
    }

    [Theory]
    [InlineData("ăâđêôơưĂÂĐÊÔƠƯ", "aadeoouAADEOOU")]
    [InlineData("àảãáạằẳẵắặầẩẫấậ", "aaaaaaaaaaaaaaa")]
    [InlineData("èẻẽéẹềểễếệ", "eeeeeeeeee")]
    [InlineData("ìỉĩíị", "iiiii")]
    [InlineData("òỏõóọồổỗốộờởỡớợ", "ooooooooooooooo")]
    [InlineData("ùủũúụừửữứự", "uuuuuuuuuu")]
    [InlineData("ỳỷỹýỵ", "yyyyy")]
    [InlineData("HD\x200B000\x00AD123", "HD000123")]
    [InlineData("HD\u0000000\u007F123", "HD000123")]
    [InlineData("HD000123\x2028noi\x00A0dung\x3000x", "HD000123 noi dung x")]
    public void Content_becomes_ascii_whatever_it_holds(string content, string expected) =>
        Assert.Equal(expected, VietQrPayload.ContentOf(content));

    [Fact]
    public void Decomposed_and_composed_content_give_one_text() =>
        Assert.Equal(
            VietQrPayload.ContentOf("Hàu Vân Đồn".Normalize(NormalizationForm.FormC)),
            VietQrPayload.ContentOf("Hàu Vân Đồn".Normalize(NormalizationForm.FormD)));

    [Theory]
    [InlineData("1234567890123456789012345", 25)]
    [InlineData("12345678901234567890123456", 25)]
    [InlineData("123456789012345678901234 6", 24)]
    [InlineData("ấấấấấấấấấấấấấấấấấấấấấấấấấấấấấấ", 25)]
    public void A_cut_never_leaves_a_space_or_a_wrong_length(string content, int length)
    {
        var clean = VietQrPayload.ContentOf(content);
        Assert.Equal(length, clean.Length);
        Assert.False(char.IsWhiteSpace(clean[^1]));

        var additional = Read(Value(Read(VietQrPayload.Build("970436", Account, 1000, content)), "62"));
        Assert.Equal(clean, Assert.Single(additional).Value);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\x200B\x200C")]
    [InlineData("\x0301\x0301")]
    public void Content_with_nothing_left_is_refused(string content) =>
        Assert.ThrowsAny<ArgumentException>(() => VietQrPayload.Build("970436", Account, 1000, content));

    // A value that could break the structure never reaches a field.
    [Theory]
    [InlineData("TEST\u0000001")]
    [InlineData("TEST 0001")]
    [InlineData("TEST0001\n")]
    [InlineData("TEST\x200B0001")]
    [InlineData("١٢٣٤٥٦")]
    [InlineData("１２３４５６")]
    [InlineData("TEST6304")]
    public void An_account_that_is_not_plain_letters_and_digits_is_refused_or_kept_whole(string account)
    {
        if (account == "TEST6304")
        {
            Assert.Equal(account, Value(Read(Value(Read(Value(Read(VietQrPayload.Build("970436", account, 1000, "HD1")), "38")), "01")), "01"));
            return;
        }

        var refused = Assert.ThrowsAny<ArgumentException>(() => VietQrPayload.Build("970436", account, 1000, "HD1"));
        Assert.DoesNotContain(account, refused.Message);
    }

    [Theory]
    [InlineData("９７０４３６")]
    [InlineData("٩٧٠٤٣٦")]
    [InlineData("97043\n")]
    [InlineData("970 36")]
    public void A_bin_that_is_not_six_ascii_digits_is_refused(string bin) =>
        Assert.ThrowsAny<ArgumentException>(() => VietQrPayload.Build(bin, Account, 1000, "HD1"));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(10_000_000_000_000L)]
    [InlineData(long.MaxValue)]
    public void An_amount_outside_the_field_is_refused(long amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VietQrPayload.Build("970436", Account, amount, "HD1"));

    [Fact]
    public void An_amount_is_plain_digits()
    {
        var amount = Value(Read(VietQrPayload.Build("970436", Account, 1_234_567, "HD1")), "54");

        Assert.Equal("1234567", amount);
    }

    // After the move to Masking: the same results as the old EmployeeInput formula, written out by hand.
    [Theory]
    [InlineData("A", "A")]
    [InlineData("ABCD", "ABCD")]
    [InlineData("ABCDE", "*BCDE")]
    [InlineData("TESTQA000001", "********0001")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZ", "****************ZZZZ")]
    public void Employee_masking_is_unchanged_after_the_move(string number, string expected)
    {
        Assert.Equal(expected, EmployeeInput.MaskAccountNumber(number));
        Assert.Equal(expected, Masking.AccountNumber(number));
    }

    /// <summary>A separate reader: the text must end exactly on a field and every value must fit its length.</summary>
    private static List<(string Id, string Value)> Read(string text)
    {
        var fields = new List<(string, string)>();
        for (var at = 0; at < text.Length;)
        {
            var length = int.Parse(text.Substring(at + 2, 2), CultureInfo.InvariantCulture);
            fields.Add((text.Substring(at, 2), text.Substring(at + 4, length)));
            at += 4 + length;
        }

        return fields;
    }

    private static string Value(List<(string Id, string Value)> fields, string id) => fields.Single(f => f.Id == id).Value;

    /// <summary>Written here again, with the table form of the same CRC, so the check does not use the code under test.</summary>
    private static string Crc(string text)
    {
        var crc = 0xFFFF;
        foreach (var value in Encoding.ASCII.GetBytes(text))
        {
            crc = ((crc << 8) & 0xFFFF) ^ Table[((crc >> 8) ^ value) & 0xFF];
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }

    private static readonly int[] Table = Enumerable.Range(0, 256).Select(index =>
    {
        var entry = index << 8;
        for (var bit = 0; bit < 8; bit++)
        {
            entry = (entry & 0x8000) != 0 ? ((entry << 1) ^ 0x1021) & 0xFFFF : (entry << 1) & 0xFFFF;
        }

        return entry;
    }).ToArray();
}
