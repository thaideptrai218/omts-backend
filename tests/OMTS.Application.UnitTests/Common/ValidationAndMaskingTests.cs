using OMTS.Application.Common.Validation;
using OMTS.Application.Common;

namespace OMTS.Application.UnitTests.Common;

public class ValidationAndMaskingTests
{
    [Theory]
    [InlineData("0912345678", "0912345678")]
    [InlineData("+84912345678", "0912345678")]
    [InlineData("84912345678", "0912345678")]
    [InlineData("091 234.56-78", "0912345678")]
    [InlineData("091234567", null)]
    [InlineData("1912345678", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Phone_numbers_are_normalised_to_ten_digits_starting_with_0(string input, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(input));

    [Theory]
    [InlineData("Secret12", true)]
    [InlineData("secret12", true)]
    [InlineData("Secret1", false)]
    [InlineData("SecretSecret", false)]
    [InlineData("12345678", false)]
    public void Password_needs_8_characters_with_a_letter_and_a_digit(string password, bool valid) =>
        Assert.Equal(valid, PasswordPolicy.IsValid(password));

    [Fact]
    public void Contacts_are_masked_for_logs_and_messages()
    {
        Assert.Equal("091****678", Masking.Phone("0912345678"));
        Assert.Equal("n***@mail.com", Masking.Email("nguyen@mail.com"));
    }
}
