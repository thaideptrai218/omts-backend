using static OMTS.Application.Features.Notifications.NotificationRole;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// PM decision OMTS-216 (03/11): a mandatory type (BR-ACCOUNT-11) also goes out as one SMS to the recipients of these
/// roles, the Owner and, for the water alert only, the Zone Managers of the zone. A role not listed keeps the
/// notification and the push and gets no SMS. A type joins by naming its rule in <see cref="NotificationTypes.All"/>.
/// </summary>
/// <param name="To">The roles that get the SMS; the other recipients of the notification do not.</param>
/// <param name="AmountParameter">Money types: the name of the amount in the message parameters; null for the others.</param>
/// <param name="ConfirmedAmountParameter">
/// Money types of a message that may hold two amounts (a list of confirmations and cancellations, PM answer 12/11/2026, OMTS-224 [1.4]): the name of the total confirmed in the message
/// parameters. With <paramref name="CancelledAmountParameter"/> and a message that has both, the SMS shows the two apart ("thu X VND, huy Y VND", a part of 0 left out) instead of
/// <paramref name="AmountParameter"/>, which stays their sum for the push and the list.
/// </param>
/// <param name="CancelledAmountParameter">The name of the total cancelled; see <paramref name="ConfirmedAmountParameter"/>.</param>
public sealed record NotificationSmsRule(
    IReadOnlyList<NotificationRole> To, string? AmountParameter = null, string? ConfirmedAmountParameter = null, string? CancelledAmountParameter = null)
{
    /// <summary>An operational type: the SMS is the MSG text. Never for a money type (<see cref="MoneyToOwner"/>).</summary>
    public static NotificationSmsRule MessageTextTo(params NotificationRole[] to) => new(to);

    /// <summary>A money type: to the Owner, with the document code (the related record) and the amount only, never a bank account number.</summary>
    public static NotificationSmsRule MoneyToOwner(string amountParameter) => new([CooperativeOwner], amountParameter);

    /// <summary>A money type to the Owner whose message may hold a confirmed and a cancelled total: the SMS shows them apart (see <see cref="ConfirmedAmountParameter"/>).</summary>
    public static NotificationSmsRule MoneyToOwnerSplit(string amountParameter, string confirmedAmountParameter, string cancelledAmountParameter) =>
        new([CooperativeOwner], amountParameter, confirmedAmountParameter, cancelledAmountParameter);
}
