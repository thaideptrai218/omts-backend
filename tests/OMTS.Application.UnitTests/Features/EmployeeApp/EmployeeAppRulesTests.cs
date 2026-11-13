using OMTS.Application.Common.Messages;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.UnitTests.Features.EmployeeApp;

/// <summary>LABOR-03 and LABOR-06: who counts as Employed today, the SRS texts of the login code messages, and the type of the issuer's notification.</summary>
public class EmployeeAppRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static Employee Worker(int statusId, DateOnly? lastWorkingDay) => new() { StatusId = statusId, LastWorkingDay = lastWorkingDay };

    [Fact]
    public void An_employee_is_employed_today_until_the_end_of_their_last_working_day()
    {
        Assert.True(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Working, null), Today));
        Assert.True(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Working, Today), Today));
        Assert.True(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Working, Today.AddDays(3)), Today));
        // The day after the last working day, before the daily job has recorded it.
        Assert.False(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Working, Today.AddDays(-1)), Today));
        Assert.False(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Left, null), Today));
        Assert.False(EmployeeAppAccess.IsEmployedToday(Worker(StatusIds.Employee.Left, Today.AddDays(-1)), Today));
    }

    [Fact]
    public void The_login_code_message_reads_as_msg_labor_08_with_the_expiry_in_vietnam_time_and_names_its_values()
    {
        var message = UserMessages.LoginCodeIssued("Nguyen Van A", "123456", new DateTimeOffset(2026, 11, 11, 7, 5, 0, TimeSpan.Zero), "0000000000");

        Assert.Equal("MSG-LABOR-08", message.Code);
        Assert.Equal(
            "Login code for Nguyen Van A: 123456. It works once and expires at 14:05 11/11/2026. Give it to Nguyen Van A in person or by calling 0000000000.",
            message.Text);
        Assert.Null(message.LegacyCode);
        Assert.Equal("123456", message.Parameters!["code"]);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeSpan.FromHours(7)), message.Parameters["expiry_time"]);
        Assert.Equal(["employee_name", "code", "expiry_time", "phone_number"], message.Parameters.Keys);
    }

    [Fact]
    public void The_refusals_of_the_login_code_read_as_msg_labor_09_17_18_and_19()
    {
        var left = UserMessages.LoginCodeNotIssued("Nguyen Van A", "the employee has left");

        Assert.Equal(("MSG-LABOR-09", "No login code can be issued for Nguyen Van A: the employee has left."), (left.Code, left.Text));
        Assert.Equal(
            ("MSG-LABOR-17", "This phone is not linked to you yet. Ask your Zone Manager for a login code, then enter it here."),
            (UserMessages.DeviceNotLinked.Code, UserMessages.DeviceNotLinked.Text));
        Assert.Equal(
            ("MSG-LABOR-18", "This phone number cannot log in to the employee app. Please contact your Zone Manager."),
            (UserMessages.EmployeePhoneNotRegistered.Code, UserMessages.EmployeePhoneNotRegistered.Text));
        Assert.Equal(
            ("MSG-LABOR-19", "This login code is wrong or can no longer be used. Check it, or ask your Zone Manager for a new code."),
            (UserMessages.LoginCodeRejected.Code, UserMessages.LoginCodeRejected.Text));
    }

    // L5: only exactly 6 ASCII digits can be a code; anything else is refused before the code is read and costs no try.
    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("abcdef", false)]
    [InlineData("12-345", false)]
    [InlineData("12 456", false)]
    [InlineData("١٢٣٤٥٦", false)] // Arabic-Indic digits are digits to char.IsDigit but not codes
    [InlineData("", false)]
    public void Only_six_ascii_digits_are_a_well_formed_login_code(string entry, bool expected) =>
        Assert.Equal(expected, EmployeeAppLoginService.IsWellFormed(entry));

    // L8: the device name is shown to the Zone Manager: no control characters, trimmed, capped.
    [Fact]
    public void A_device_name_loses_control_characters_and_outer_spaces_and_is_capped()
    {
        Assert.Equal("Pixel 7", EmployeeAppLoginService.CleanDeviceName("  Pixel\u0000 7\r\n "));
        Assert.Null(EmployeeAppLoginService.CleanDeviceName(null));
        Assert.Null(EmployeeAppLoginService.CleanDeviceName(" \t\n"));
        Assert.Equal(200, EmployeeAppLoginService.CleanDeviceName(new string('a', 300))!.Length);
    }

    // M2: the issuer is told, under the code of a code that cannot be used, and the text holds no code.
    [Fact]
    public void The_message_to_the_issuer_of_a_cancelled_code_names_the_employee_and_no_code()
    {
        var message = UserMessages.LoginCodeCancelledByWrongEntries("Nguyen Van A");

        Assert.Equal("MSG-LABOR-19", message.Code);
        Assert.Contains("Nguyen Van A", message.Text);
        Assert.Contains("5 wrong entries", message.Text);
        Assert.DoesNotContain(message.Text.Split(' '), word => word.Length >= 6 && word.All(char.IsDigit));
        Assert.Equal(["employee_name", "reason"], message.Parameters!.Keys);
    }

    // BR-LABOR-02, BR-LABOR-04: the code goes to the issuer's account as a normal type (push unless turned off), never by SMS and never to the employee.
    [Fact]
    public void The_notification_of_the_code_is_a_normal_type_of_the_owner_and_the_zone_manager_with_no_sms()
    {
        var type = NotificationTypes.Find(NotificationTypes.EmployeeLoginCode);

        Assert.NotNull(type);
        Assert.False(type.IsMandatory);
        Assert.Equal(NotificationPushPolicy.Default, type.PushPolicy);
        Assert.Equal([NotificationRole.CooperativeOwner, NotificationRole.ZoneManager], type.Roles);
        Assert.Null(type.Sms);
        Assert.DoesNotContain(NotificationRole.Trader, type.Roles);
        Assert.DoesNotContain(NotificationRole.Accountant, type.Roles);
    }
}
