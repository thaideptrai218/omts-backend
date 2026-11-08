using OMTS.Application.Features.Notifications;
using Xunit.Sdk;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// QA of the guard test <c>Every_mandatory_type_names_its_sms_and_no_other_type_has_one</c> without touching the catalogue:
/// its exact assertion runs on lists with a rule taken out or added. Also the catalogue of this branch against the decision.
/// </summary>
public class MandatorySmsGuardQaTests
{
    private static readonly NotificationOpenAccess Open = NotificationOpenAccess.OwnerOnly;

    // The assertion of the guard test, word for word.
    private static void Guard(IReadOnlyList<NotificationTypeDefinition> types) =>
        Assert.All(types, t => Assert.Equal(t.IsMandatory, t.Sms is not null));

    [Fact]
    public void The_real_catalogue_passes_the_guard()
    {
        Guard(NotificationTypes.All);
    }

    // As feature/OMTS-218-payments-debts registers SUPPLIER_TRANSACTION today: AlwaysPush with no rule. The guard stops the merge.
    [Fact]
    public void The_guard_fails_when_a_mandatory_type_has_no_rule()
    {
        var withoutRule = new NotificationTypeDefinition("SUPPLIER_TRANSACTION", "Supplier payment transaction", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner], Open);

        Assert.Throws<AllException>(() => Guard([.. NotificationTypes.All, withoutRule]));
    }

    [Theory]
    [InlineData(NotificationTypes.WaterAlert)]
    [InlineData(NotificationTypes.WaterAlertWithdrawn)]
    [InlineData(NotificationTypes.ZoneLicenseExpiry)]
    public void The_guard_fails_when_a_rule_of_the_catalogue_is_removed(string code)
    {
        var doctored = NotificationTypes.All.Select(t => t.Code == code ? t with { Sms = null } : t).ToList();

        Assert.Throws<AllException>(() => Guard(doctored));
    }

    [Theory]
    [InlineData(NotificationPushPolicy.Default)]
    [InlineData(NotificationPushPolicy.NeverPush)]
    public void The_guard_fails_when_a_type_that_is_not_mandatory_gets_a_rule(NotificationPushPolicy policy)
    {
        var withRule = new NotificationTypeDefinition("NORMAL", "Normal", policy, [NotificationRole.CooperativeOwner], Open, NotificationSmsRule.MessageTextTo(NotificationRole.CooperativeOwner));

        Assert.Throws<AllException>(() => Guard([withRule]));
    }

    // Observations: what the guard does not see. A rule is present, but it reaches nobody or a role the type never reaches.
    [Fact]
    public void Observation_the_guard_passes_a_rule_that_reaches_nobody_or_a_role_outside_the_type()
    {
        var nobody = new NotificationTypeDefinition("A", "A", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner], Open, new NotificationSmsRule([]));
        var otherRole = new NotificationTypeDefinition("B", "B", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner], Open, NotificationSmsRule.MessageTextTo(NotificationRole.ZoneManager));

        Guard([nobody, otherRole]);
    }

    // The guard follows IsMandatory, not BR-ACCOUNT-11: a mandatory type of the SRS declared Default passes with no rule.
    [Fact]
    public void Observation_the_guard_passes_an_srs_mandatory_type_declared_with_the_default_push()
    {
        Guard([new NotificationTypeDefinition("NEW_PURCHASE_REQUEST", "New purchase request", NotificationPushPolicy.Default, [NotificationRole.CooperativeOwner], Open)]);
    }

    [Fact]
    public void Each_rule_of_the_catalogue_is_the_decision_and_names_only_roles_of_its_type()
    {
        Assert.All(NotificationTypes.All.Where(t => t.Sms is not null), t =>
        {
            Assert.NotEmpty(t.Sms!.To);
            Assert.All(t.Sms.To, role => Assert.Contains(role, t.Roles));
            Assert.Contains(NotificationRole.CooperativeOwner, t.Sms.To);
            Assert.DoesNotContain(NotificationRole.Accountant, t.Sms.To);
            Assert.DoesNotContain(NotificationRole.Trader, t.Sms.To);
        });
    }

    [Fact]
    public void The_mandatory_types_are_the_three_of_the_change_list_the_certification_expiry_the_new_purchase_request_the_cycle_close_reminder_and_the_two_transactions_of_the_accountant()
    {
        Assert.Equal(
            [NotificationTypes.WaterAlert, NotificationTypes.WaterAlertWithdrawn, NotificationTypes.ZoneLicenseExpiry, NotificationTypes.CooperativeCertificationExpiry, NotificationTypes.PurchaseRequestNew, NotificationTypes.CycleCloseReminder, NotificationTypes.SupplierTransaction, NotificationTypes.CustomerPaymentTransaction],
            NotificationTypes.All.Where(t => t.IsMandatory).Select(t => t.Code));
    }

    [Fact]
    public void A_money_rule_names_the_owner_only()
    {
        var rule = NotificationSmsRule.MoneyToOwner("amount");

        Assert.Equal([NotificationRole.CooperativeOwner], rule.To);
        Assert.Equal("amount", rule.AmountParameter);
    }
}
