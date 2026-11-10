using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Employees;

/// <summary>EMP-API-06: the optional fields code, gender, ID number, email and start date of <see cref="EmployeeInput"/>; the rules of the other fields are those of SaveEmployeeRequest.</summary>
public class EmployeeInputNewFieldsTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static SaveEmployeeRequest Request(
        string? code = null, Gender? gender = null, string? idNumber = null, string? email = null, DateOnly? startDate = null) =>
        new("Trần Văn A", "0912345678", null, null, null, null, null, null, [Guid.NewGuid()], null, EmployeeType.SeasonalWorker,
            Code: code, Gender: gender, IdNumber: idNumber, Email: email, StartDate: startDate);

    private static (EmployeeInput? Input, List<string> Invalid) Check(SaveEmployeeRequest request, bool adding = true) =>
        EmployeeInput.From(request, Today, adding);

    [Fact]
    public void A_client_that_does_not_know_the_fields_sends_none_and_is_accepted()
    {
        var (input, invalid) = Check(Request());

        Assert.Empty(invalid);
        Assert.Equal((null, null, null, null, null), (input!.Code, input.Gender, input.IdNumber, input.Email, input.StartDate));
    }

    [Fact]
    public void Valid_values_are_normalised()
    {
        var (input, invalid) = Check(Request(" nv-7 ", Gender.Female, " 079090001234 ", "  An@Example.COM ", Today));

        Assert.Empty(invalid);
        Assert.Equal(("NV-7", Gender.Female, "079090001234", "an@example.com", Today), (input!.Code, input.Gender, input.IdNumber, input.Email, input.StartDate));
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("123456789012")]
    public void An_id_number_has_9_or_12_digits(string number) => Assert.Empty(Check(Request(idNumber: number)).Invalid);

    [Theory]
    [InlineData("12345678")]
    [InlineData("1234567890")]
    [InlineData("1234567890123")]
    [InlineData("12345678901a")]
    public void Any_other_id_number_is_refused(string number) => Assert.Equal(["ID number"], Check(Request(idNumber: number)).Invalid);

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@example.com")]
    public void An_invalid_email_is_refused(string email) => Assert.Equal(["Email"], Check(Request(email: email)).Invalid);

    [Fact]
    public void An_email_of_more_than_254_characters_is_refused()
    {
        var local = new string('a', 64);
        var tooLong = $"{local}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 63)}.com";

        Assert.True(tooLong.Length > 254);
        Assert.Equal(["Email"], Check(Request(email: tooLong)).Invalid);
    }

    [Fact]
    public void The_start_date_may_be_today_but_not_later()
    {
        Assert.Empty(Check(Request(startDate: Today)).Invalid);
        Assert.Equal(["Start date"], Check(Request(startDate: Today.AddDays(1))).Invalid);
    }

    [Fact]
    public void A_gender_that_is_not_defined_is_refused() => Assert.Equal(["Gender"], Check(Request(gender: (Gender)9)).Invalid);

    [Fact]
    public void A_code_with_other_characters_is_refused() => Assert.Equal(["Code"], Check(Request(code: "NV 1")).Invalid);

    [Fact]
    public void An_empty_text_clears_the_id_number_and_email_of_an_edit_while_null_leaves_them()
    {
        var (cleared, _) = Check(Request(idNumber: "", email: " "), adding: false);
        var (kept, _) = Check(Request(), adding: false);

        Assert.Equal(("", ""), (cleared!.IdNumber, cleared.Email));
        Assert.Equal((null, null), (kept!.IdNumber, kept.Email));
    }
}
