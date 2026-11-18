using OMTS.Application.Features.Parties;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Parties;

public sealed class PartyInvoiceRulesTests
{
    private static readonly AddressParts PickedAddress = new(1, "Thành phố Hà Nội", 10, "Phường Cầu Giấy", "Số 5, Phố Nguyễn Hữu An");

    private static InvoiceInfoInput Input(
        InvoiceBuyerType? type, string? name = "Công ty ABC", string? taxCode = "0100109106", string? personal = null, bool? same = true,
        AddressParts? address = null, string? email = "ketoan@abc.vn") =>
        new(type, name, taxCode, personal, same, address ?? AddressParts.Empty, email);

    private static (InvoiceInfo Info, List<string> Invalid) Check(
        InvoiceInfoInput input, AddressParts? contact = null, InvoiceBuyerType fallback = InvoiceBuyerType.Consumer, bool allowConsumer = true)
    {
        var invalid = new List<string>();
        var info = PartyInvoiceRules.Invoice(input, contact ?? PickedAddress, fallback, allowConsumer, invalid);
        return (info, invalid);
    }

    [Fact]
    public void A_consumer_has_no_invoice_info_and_what_is_sent_is_dropped()
    {
        var (info, invalid) = Check(Input(InvoiceBuyerType.Consumer, personal: "012345678901", same: false, address: PickedAddress), contact: AddressParts.Empty);

        Assert.Empty(invalid);
        Assert.Equal(new InvoiceInfo(InvoiceBuyerType.Consumer, null, null, null, true, AddressParts.Empty, null), info);
    }

    [Fact]
    public void Nothing_sent_is_a_consumer_for_a_customer()
    {
        var (info, invalid) = Check(Input(null, name: null, taxCode: null, email: null), contact: AddressParts.Empty);

        Assert.Empty(invalid);
        Assert.Equal(InvoiceBuyerType.Consumer, info.BuyerType);
    }

    [Fact]
    public void An_organization_with_name_tax_code_email_and_address_is_accepted()
    {
        var (info, invalid) = Check(Input(InvoiceBuyerType.Organization, name: "  Công   ty ABC "));

        Assert.Empty(invalid);
        Assert.Equal(("Công ty ABC", "0100109106", "ketoan@abc.vn", true), (info.Name, info.TaxCode, info.Email, info.AddressSameAsContact));
    }

    [Fact]
    public void An_organization_needs_every_one_of_name_tax_code_email_and_address()
    {
        var (_, invalid) = Check(Input(InvoiceBuyerType.Organization, name: null, taxCode: null, email: null), contact: AddressParts.Empty);

        Assert.Equal(["Invoice name", "Tax code", "Invoice email", "Province"], invalid);
    }

    [Theory]
    [InlineData("0100109106", true)]
    [InlineData("0100109106-001", true)]
    [InlineData("010010910", false)]
    [InlineData("012345678901", false)]
    [InlineData("0100109106-01", false)]
    [InlineData("abc", false)]
    public void The_tax_code_of_an_organization_is_10_digits_with_an_optional_branch(string taxCode, bool valid)
    {
        var (_, invalid) = Check(Input(InvoiceBuyerType.Organization, taxCode: taxCode));

        Assert.Equal(valid, !invalid.Contains("Tax code"));
    }

    [Fact]
    public void The_email_of_an_organization_must_be_an_address()
    {
        var (_, invalid) = Check(Input(InvoiceBuyerType.Organization, email: "not an email"));

        Assert.Equal(["Invoice email"], invalid);
    }

    [Fact]
    public void An_organization_keeps_no_personal_tax_id_and_an_individual_keeps_no_organization_tax_code()
    {
        var (organization, _) = Check(Input(InvoiceBuyerType.Organization, personal: "012345678901"));
        var (individual, invalid) = Check(Input(InvoiceBuyerType.Individual, name: "Nguyễn Văn An", taxCode: "0100109106", email: null));

        Assert.Null(organization.PersonalTaxId);
        Assert.Empty(invalid);
        Assert.Null(individual.TaxCode);
    }

    [Fact]
    public void An_individual_needs_only_a_name_and_an_address()
    {
        var (info, invalid) = Check(Input(InvoiceBuyerType.Individual, name: "Nguyễn Văn An", taxCode: null, email: null));

        Assert.Empty(invalid);
        Assert.Equal(("Nguyễn Văn An", null, null, null), (info.Name, info.PersonalTaxId, info.Email, info.TaxCode));

        var (_, missing) = Check(Input(InvoiceBuyerType.Individual, name: null, taxCode: null, email: null), contact: AddressParts.Empty);
        Assert.Equal(["Invoice name", "Province"], missing);
    }

    [Theory]
    [InlineData("0312345678", true)]
    [InlineData("012345678901", true)]
    [InlineData("031234567", false)]
    [InlineData("01234567890", false)]
    [InlineData("01234567890A", false)]
    public void The_personal_tax_id_of_an_individual_is_10_or_12_digits_when_given(string personal, bool valid)
    {
        var (info, invalid) = Check(Input(InvoiceBuyerType.Individual, name: "An", taxCode: null, personal: personal, email: null));

        Assert.Equal(valid, !invalid.Contains("Personal tax ID"));
        Assert.Equal(valid ? personal : null, info.PersonalTaxId is { } id && valid ? id : null);
    }

    [Fact]
    public void The_email_of_an_individual_is_optional_but_must_be_an_address_when_given()
    {
        var (_, invalid) = Check(Input(InvoiceBuyerType.Individual, name: "An", taxCode: null, email: "nope"));

        Assert.Equal(["Invoice email"], invalid);
    }

    [Fact]
    public void A_supplier_is_never_a_consumer()
    {
        var (_, invalid) = Check(Input(InvoiceBuyerType.Consumer), fallback: InvoiceBuyerType.Individual, allowConsumer: false);

        Assert.Equal(["Invoice buyer type"], invalid);
    }

    [Fact]
    public void An_undefined_buyer_type_is_refused()
    {
        var (_, invalid) = Check(Input((InvoiceBuyerType)99));

        Assert.Equal(["Invoice buyer type"], invalid);
    }

    [Fact]
    public void The_invoice_address_as_the_contact_address_needs_province_ward_and_street_and_accepts_an_old_text_entry()
    {
        var text = new AddressParts(null, "Hà Nội", null, "Cầu Giấy", "Số 5");

        Assert.Empty(Check(Input(InvoiceBuyerType.Organization), contact: text).Invalid);
        Assert.Equal(["Province"], Check(Input(InvoiceBuyerType.Organization), contact: text with { ProvinceName = null }).Invalid);
        Assert.Equal(["Ward"], Check(Input(InvoiceBuyerType.Organization), contact: text with { WardName = null }).Invalid);
        Assert.Equal(["Address"], Check(Input(InvoiceBuyerType.Organization), contact: PickedAddress with { Street = null }).Invalid);
    }

    [Fact]
    public void A_separate_invoice_address_is_picked_by_itself_and_the_contact_address_may_stay_empty()
    {
        var (info, invalid) = Check(Input(InvoiceBuyerType.Organization, same: false, address: PickedAddress), contact: AddressParts.Empty);

        Assert.Empty(invalid);
        Assert.False(info.AddressSameAsContact);
        Assert.Equal(PickedAddress, info.Address);
    }

    [Fact]
    public void A_separate_invoice_address_that_is_not_complete_names_the_first_missing_part()
    {
        Assert.Equal(["Invoice province"], Check(Input(InvoiceBuyerType.Organization, same: false)).Invalid);
        Assert.Equal(["Invoice ward"], Check(Input(InvoiceBuyerType.Organization, same: false, address: PickedAddress with { WardCode = null, WardName = null })).Invalid);
        Assert.Equal(["Invoice street"], Check(Input(InvoiceBuyerType.Organization, same: false, address: PickedAddress with { Street = " " })).Invalid);
    }

    [Fact]
    public void The_invoice_address_is_empty_while_it_is_the_contact_address()
    {
        var (info, _) = Check(Input(InvoiceBuyerType.Organization, same: true, address: PickedAddress with { Street = "Another street" }));

        Assert.Equal(AddressParts.Empty, info.Address);
    }

    [Fact]
    public void A_contact_address_may_be_left_empty_or_kept_as_old_text()
    {
        var invalid = new List<string>();

        Assert.Equal(AddressParts.Empty, PartyInvoiceRules.Contact(null, null, null, null, null, invalid));
        var old = PartyInvoiceRules.Contact(null, "Hà Nội", null, "Cầu Giấy", "Số 5", invalid);

        Assert.Empty(invalid);
        Assert.True(old.NeedsReview);
        Assert.True(old.IsComplete);
    }

    [Theory]
    [InlineData(null, "Hà Nội", 10, "Cầu Giấy", "Ward")]
    [InlineData(1, "Hà Nội", 10, null, "Ward")]
    [InlineData(1, null, null, null, "Province")]
    [InlineData(0, "Hà Nội", null, null, "Province")]
    [InlineData(1, "Hà Nội", -5, "Cầu Giấy", "Ward")]
    public void A_code_needs_its_name_and_a_ward_needs_a_province(int? provinceCode, string? province, int? wardCode, string? ward, string field)
    {
        var invalid = new List<string>();

        PartyInvoiceRules.Contact(provinceCode, province, wardCode, ward, null, invalid);

        Assert.Equal([field], invalid);
    }

    [Fact]
    public void Too_long_entries_are_refused()
    {
        var invalid = new List<string>();

        PartyInvoiceRules.Contact(null, new string('a', 101), null, new string('b', 101), new string('c', 501), invalid);

        Assert.Equal(["Province", "Ward", "Address"], invalid);
    }

    [Fact]
    public void An_address_is_complete_with_province_ward_and_street_and_needs_review_without_a_code()
    {
        Assert.True(PickedAddress.IsComplete);
        Assert.False(PickedAddress.NeedsReview);
        Assert.False((PickedAddress with { WardName = null, WardCode = null }).IsComplete);
        Assert.True((PickedAddress with { WardCode = null }).IsComplete);
        Assert.True((PickedAddress with { WardCode = null }).NeedsReview);
        Assert.True((PickedAddress with { ProvinceCode = null }).NeedsReview);
    }

    [Fact]
    public async Task A_ward_of_another_province_is_refused_and_the_names_of_the_list_replace_the_ones_sent()
    {
        var verifier = new PartyAddressVerifier(AddressTestData.Service(new FakeAdministrativeUnitSource()));
        var invalid = new List<string>();

        var wrongWard = await verifier.VerifyAsync(PickedAddress with { WardCode = 300 }, "Province", "Ward", invalid, CancellationToken.None);
        var unknownProvince = await verifier.VerifyAsync(PickedAddress with { ProvinceCode = 999 }, "Invoice province", "Invoice ward", invalid, CancellationToken.None);
        var good = await verifier.VerifyAsync(PickedAddress with { ProvinceName = "ha noi", WardName = "cau giay" }, "Province", "Ward", new List<string>(), CancellationToken.None);

        Assert.Equal(["Ward", "Invoice province"], invalid);
        Assert.Equal(300, wrongWard.WardCode);
        Assert.Equal(("Thành phố Hà Nội", "Phường Cầu Giấy"), (good.ProvinceName, good.WardName));
    }

    [Fact]
    public async Task The_ward_check_is_skipped_while_the_list_is_down_and_an_address_with_no_code_is_not_checked()
    {
        var verifier = new PartyAddressVerifier(AddressTestData.Service(new FakeAdministrativeUnitSource { Down = true }));
        var invalid = new List<string>();

        var kept = await verifier.VerifyAsync(PickedAddress with { WardCode = 300 }, "Province", "Ward", invalid, CancellationToken.None);
        var text = await verifier.VerifyAsync(new AddressParts(null, "Hà Nội", null, "Nowhere", "Số 5"), "Province", "Ward", invalid, CancellationToken.None);

        Assert.Empty(invalid);
        Assert.Equal(PickedAddress with { WardCode = 300 }, kept);
        Assert.Equal("Nowhere", text.WardName);
    }
}
