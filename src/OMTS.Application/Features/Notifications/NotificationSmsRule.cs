using static OMTS.Application.Features.Notifications.NotificationRole;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// PM decision OMTS-216 (03/11): a mandatory type (BR-ACCOUNT-11) also goes out as one SMS to the recipients of these
/// roles, the Owner and, for the water alert only, the Zone Managers of the zone. A role not listed keeps the
/// notification and the push and gets no SMS. A type joins by naming its rule in <see cref="NotificationTypes.All"/>.
/// </summary>
/// <param name="To">The roles that get the SMS; the other recipients of the notification do not.</param>
/// <param name="AmountParameter">Money types: the name of the amount in the message parameters; null for the others.</param>
public sealed record NotificationSmsRule(IReadOnlyList<NotificationRole> To, string? AmountParameter = null)
{
    /// <summary>An operational type: the SMS is the MSG text. Never for a money type (<see cref="MoneyToOwner"/>).</summary>
    public static NotificationSmsRule MessageTextTo(params NotificationRole[] to) => new(to);

    /// <summary>A money type: to the Owner, with the document code (the related record) and the amount only, never a bank account number.</summary>
    public static NotificationSmsRule MoneyToOwner(string amountParameter) => new([CooperativeOwner], amountParameter);
}
