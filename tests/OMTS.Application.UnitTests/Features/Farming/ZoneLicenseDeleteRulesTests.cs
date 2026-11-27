using OMTS.Application.Features.Farming.Licenses;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>Which licenses may be deleted: only one outside a renewal chain.</summary>
public class ZoneLicenseDeleteRulesTests
{
    private static ZoneComplianceDocument License(string number, Guid? replaces = null) => new() { DocumentNumber = number, ReplacesDocumentId = replaces };

    [Fact]
    public void A_license_that_is_neither_replaced_nor_a_renewal_may_be_deleted() =>
        Assert.Null(ZoneLicenseDeleteRules.Refusal(License("GP-1"), replacement: null));

    [Fact]
    public void A_replaced_license_is_kept_and_the_message_names_its_replacement()
    {
        var old = License("GP-1");
        var refusal = ZoneLicenseDeleteRules.Refusal(old, License("GP-2", old.Id));

        Assert.NotNull(refusal);
        Assert.Equal("MSG-GEN-09", refusal.Code);
        Assert.Contains("GP-2", refusal.Text);
    }

    [Fact]
    public void A_license_that_renewed_another_is_kept()
    {
        var refusal = ZoneLicenseDeleteRules.Refusal(License("GP-2", Guid.NewGuid()), replacement: null);

        Assert.NotNull(refusal);
        Assert.Equal("MSG-GEN-09", refusal.Code);
    }

    [Fact]
    public void A_middle_link_of_a_chain_is_kept_for_both_reasons()
    {
        var first = License("GP-1");
        var middle = License("GP-2", first.Id);

        Assert.NotNull(ZoneLicenseDeleteRules.Refusal(middle, License("GP-3", middle.Id)));
    }
}
