using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>The mandatory types of the catalogue (the types with an SMS rule) against the decision.</summary>
public class MandatorySmsGuardQaTests
{
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
