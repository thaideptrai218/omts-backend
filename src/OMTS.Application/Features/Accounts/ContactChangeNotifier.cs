using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// After a verified contact is replaced (ACS-05), the previous one is told, so the holder notices a change
/// they did not make. Best effort: a failed send never undoes the change (the sender logs it).
/// </summary>
public sealed class ContactChangeNotifier(ISmsSender smsSender, IEmailSender emailSender)
{
    // SMS unaccented so it fits one GSM-7 segment, like the verification texts.
    private const string EmailSubject = "Thông tin liên hệ OMTS đã thay đổi";

    public async Task NotifyPreviousContactAsync(VerificationChannel channel, string? previous, string current, CancellationToken cancellationToken)
    {
        if (previous is null || previous == current)
        {
            return;
        }

        if (channel == VerificationChannel.Sms)
        {
            await smsSender.SendAsync(
                previous,
                $"OMTS: so dien thoai cua tai khoan da doi sang {Masking.Phone(current)}. Neu khong phai ban, hay lien he HTX ngay.",
                cancellationToken);
            return;
        }

        var text = $"Email của tài khoản OMTS đã được đổi sang {Masking.Email(current)}. Nếu không phải bạn thực hiện, hãy liên hệ hợp tác xã ngay.";
        await emailSender.SendAsync(previous, EmailSubject, $"<p>{text}</p>", text, cancellationToken: cancellationToken);
    }
}
