using System.Globalization;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>
/// The MSG-ORIGIN texts in Vietnamese, the language the provenance page opens in (BR-ORIGIN-02). The SRS gives the English wording only: these are the proposed texts, to be
/// confirmed by the PM. Built from the atomic parameters of the message, so both languages say the same.
/// </summary>
public static class ProvenanceVietnameseTexts
{
    /// <summary>The message in the page language: the SRS English text for "en", else the Vietnamese text with the same code and parameters.</summary>
    public static UserMessage In(this UserMessage message, string language) =>
        language == ProvenanceLanguages.English ? message : message with { Text = Vietnamese(message) };

    private static string Vietnamese(UserMessage m) => m.Code switch
    {
        MessageCodes.Origin01 =>
            $"{Text(m, "cooperative_short_name")} đã thu hồi thông tin nguồn gốc của lô {Text(m, "lot_code")} vào ngày {Text(m, "withdrawal_date")}. Để biết thêm chi tiết, vui lòng gọi {Text(m, "cooperative_phone")}.",
        MessageCodes.Origin02 => $"Hóa đơn này không có thông tin nguồn gốc để hiển thị. Để biết thêm chi tiết, vui lòng gọi {Text(m, "cooperative_phone")}.",
        MessageCodes.Origin03 => "Không tìm thấy hóa đơn của mã QR này. Vui lòng quét lại mã QR trên hóa đơn, hoặc mở đường link trong tin nhắn hóa đơn.",
        MessageCodes.Origin04 => $"Hóa đơn này đã bị hủy nên không hiển thị thông tin nguồn gốc. Để biết thêm chi tiết, vui lòng gọi {Text(m, "cooperative_phone")}.",
        _ => throw new ArgumentOutOfRangeException(nameof(m), m.Code, "Only the MSG-ORIGIN messages have a Vietnamese text."),
    };

    // A parameter as the English text shows it: dates dd/MM/yyyy, "–" for a value that is not saved.
    private static string Text(UserMessage m, string name) => m.Parameters?.GetValueOrDefault(name) switch
    {
        DateOnly date => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        null => "–",
        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "–",
    };
}
