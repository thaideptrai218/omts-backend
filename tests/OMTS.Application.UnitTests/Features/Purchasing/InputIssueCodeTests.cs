using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The code of an input issue: XD for a use, XH for a disposal, at least six digits.</summary>
public sealed class InputIssueCodeTests
{
    [Theory]
    [InlineData(InputIssueKind.Use, 1, "XD000001")]
    [InlineData(InputIssueKind.Use, 123, "XD000123")]
    [InlineData(InputIssueKind.Disposal, 7, "XH000007")]
    [InlineData(InputIssueKind.Disposal, 1000000, "XH1000000")]
    public void The_code_is_the_prefix_of_the_kind_and_at_least_six_digits(InputIssueKind kind, long number, string expected) =>
        Assert.Equal(expected, InputIssueCodeService.Format(kind, number));
}
