using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>Decisions 2 and 3 of OMTS-216: who gets the SMS of a mandatory notification, per type.</summary>
public class MandatorySmsQueueTests
{
    private readonly MandatorySmsQueueFakes _sms = new();

    // A money type as the sibling branches will register it (SUPPLIER_TRANSACTION): this branch has none yet.
    private static readonly NotificationTypeDefinition MoneyType = new(
        "SUPPLIER_TRANSACTION", "Supplier payment", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner],
        NotificationOpenAccess.OwnerOnly, NotificationSmsRule.MoneyToOwner("amount"));

    private static NotificationTypeDefinition Type(string code) => NotificationTypes.Find(code)!;

    private static NotificationContent Content(string type) => new(type, "Title", new UserMessage("MSG-TEST", "Body"));

    [Fact]
    public async Task The_water_alert_goes_to_the_owners_and_zone_managers_among_its_recipients()
    {
        var (owner, otherOwner, manager) = (_sms.Owner(), _sms.Owner(), _sms.ZoneManager());

        await QueueAsync(NotificationTypes.WaterAlert, owner.AccountId, otherOwner.AccountId, manager.AccountId);

        Assert.Equal([owner.AccountId, otherOwner.AccountId, manager.AccountId], _sms.Queued.Select(m => m.RecipientAccountId));
        Assert.All(_sms.Queued, m => Assert.Equal("Body", m.Text));
    }

    [Fact]
    public async Task A_withdrawal_reaches_everyone_the_water_alert_reached()
    {
        var (owner, manager) = (_sms.Owner(), _sms.ZoneManager());

        await QueueAsync(NotificationTypes.WaterAlertWithdrawn, owner.AccountId, manager.AccountId);

        Assert.Equal([owner.AccountId, manager.AccountId], _sms.Queued.Select(m => m.RecipientAccountId));
    }

    // The recipients of the license reminder are everyone holding CULTIVATION_CYCLES View in the zones, also Zone Managers.
    [Fact]
    public async Task A_license_reminder_goes_to_the_owner_only_though_a_zone_manager_keeps_the_notification()
    {
        var (owner, manager, accountant) = (_sms.Owner(), _sms.ZoneManager(), _sms.Accountant());

        await QueueAsync(NotificationTypes.ZoneLicenseExpiry, owner.AccountId, manager.AccountId, accountant.AccountId);

        Assert.Equal(owner.AccountId, Assert.Single(_sms.Queued).RecipientAccountId);
    }

    [Fact]
    public async Task An_accountant_among_the_recipients_of_a_water_alert_gets_no_sms()
    {
        await QueueAsync(NotificationTypes.WaterAlert, _sms.Accountant().AccountId);

        Assert.Empty(_sms.Queued);
    }

    [Theory]
    [InlineData(NotificationTypes.WaterMeasurementOverdue)]
    [InlineData(NotificationTypes.StaffAccountLockReminder)]
    public async Task A_type_that_is_not_mandatory_sends_none(string type)
    {
        await QueueAsync(type, _sms.Owner().AccountId);

        Assert.Empty(_sms.Queued);
    }

    // Decision 3: the dispatcher knows no actor; a money module does not filter, so the Owner who did the action is a recipient.
    [Fact]
    public async Task A_money_type_goes_to_the_owner_who_did_the_action_and_not_to_the_accountant()
    {
        var (actingOwner, accountant) = (_sms.Owner(), _sms.Accountant());

        await _sms.Queue.QueueAsync(
            MoneyType, Content(MoneyType.Code), [actingOwner.AccountId, accountant.AccountId], CancellationToken.None);

        Assert.Equal(actingOwner.AccountId, Assert.Single(_sms.Queued).RecipientAccountId);
    }

    // The delivery logs it after the commit, and only if a provider is configured.
    [Fact]
    public async Task An_account_without_a_verified_phone_stays_in_the_queue_with_no_number()
    {
        await QueueAsync(NotificationTypes.WaterAlert, _sms.Owner(phone: null).AccountId);

        Assert.Null(Assert.Single(_sms.Queued).PhoneNumber);
    }

    [Fact]
    public async Task A_failure_while_queueing_never_reaches_the_business_action()
    {
        var owner = _sms.Owner();
        _sms.QueryFailure = new InvalidOperationException("database down");

        await QueueAsync(NotificationTypes.WaterAlert, owner.AccountId);

        Assert.Empty(_sms.Queued);
    }

    private Task QueueAsync(string type, params Guid[] accountIds) =>
        _sms.Queue.QueueAsync(Type(type), Content(type), accountIds, CancellationToken.None);
}
