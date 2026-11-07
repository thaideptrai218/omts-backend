namespace OMTS.Application.Features.Verification;

/// <summary>Texts that carry a verification code. SMS is unaccented so it fits one GSM-7 segment.</summary>
internal static class VerificationTexts
{
    public const string EmailSubject = "Mã xác minh OMTS";

    public static string Sms(string code) => $"OMTS: ma xac minh cua ban la {code}, hieu luc 5 phut. Khong chia se ma nay cho bat ky ai.";

    public static string EmailText(string code) =>
        $"Mã xác minh OMTS của bạn là {code}. Mã có hiệu lực trong 5 phút. Không chia sẻ mã này cho bất kỳ ai.";

    public static string EmailHtml(string code) =>
        $"<p>Mã xác minh OMTS của bạn là <strong>{code}</strong>.</p><p>Mã có hiệu lực trong 5 phút. Không chia sẻ mã này cho bất kỳ ai.</p>";
}
