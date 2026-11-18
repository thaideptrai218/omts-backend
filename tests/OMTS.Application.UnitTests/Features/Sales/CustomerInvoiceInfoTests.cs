using OMTS.Application.Features.Sales.Customers;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The invoice info and the picked address of the customer form (the same rules serve the supplier form).</summary>
public sealed class CustomerInvoiceInfoTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static SaveCustomerRequest Organization() => new(
        "An", "0901234567", "Số 5, Phố Nguyễn Hữu An", null, ProvinceCode: 1, ProvinceName: "Thành phố Hà Nội", WardCode: 10, WardName: "Phường Cầu Giấy",
        InvoiceBuyerType: InvoiceBuyerType.Organization, InvoiceName: "Công ty ABC", TaxCode: "0100109106", InvoiceEmail: "ketoan@abc.vn");

    [Fact]
    public void An_organization_is_accepted_and_its_address_entries_are_read_under_both_names()
    {
        var (entries, invalid) = CustomerEntries.From(Organization(), phoneLocked: false, Today);

        Assert.Empty(invalid);
        Assert.Equal(("Số 5, Phố Nguyễn Hữu An", "Thành phố Hà Nội", 1, "Phường Cầu Giấy", 10), (entries.Address, entries.Province, entries.ProvinceCode, entries.Ward, entries.WardCode));
        Assert.Equal(("Công ty ABC", "0100109106"), (entries.Invoice.Name, entries.Invoice.TaxCode));

        var (named, namedInvalid) = CustomerEntries.From(
            new SaveCustomerRequest("An", "0901234567", null, null, Province: "Thành phố Hà Nội", ProvinceCode: 1, Ward: "Phường Cầu Giấy", WardCode: 10, Street: "Số 5"), false, Today);
        Assert.Empty(namedInvalid);
        Assert.Equal(("Số 5", "Thành phố Hà Nội", "Phường Cầu Giấy"), (named.Address, named.Province, named.Ward));
    }

    [Fact]
    public void The_company_name_of_an_older_client_is_the_invoice_name()
    {
        var (entries, invalid) = CustomerEntries.From(Organization() with { InvoiceName = null, CompanyName = "Công ty Cũ" }, phoneLocked: false, Today);

        Assert.Empty(invalid);
        Assert.Equal("Công ty Cũ", entries.Invoice.Name);
    }

    [Fact]
    public void A_customer_with_nothing_for_the_invoice_is_a_consumer_and_the_invoice_entries_are_dropped()
    {
        var (entries, invalid) = CustomerEntries.From(
            new SaveCustomerRequest("An", "0901234567", null, null, InvoiceName: "Công ty ABC", TaxCode: "0100109106", PersonalTaxId: "012345678901"), phoneLocked: false, Today);

        Assert.Empty(invalid);
        Assert.Equal(InvoiceBuyerType.Consumer, entries.Invoice.BuyerType);
        Assert.Equal((null, null, null), (entries.Invoice.Name, entries.Invoice.TaxCode, entries.Invoice.PersonalTaxId));
    }

    [Fact]
    public void An_organization_without_its_email_is_refused_and_an_old_text_address_still_saves()
    {
        var (_, noEmail) = CustomerEntries.From(Organization() with { InvoiceEmail = null }, phoneLocked: false, Today);
        var (entries, oldText) = CustomerEntries.From(Organization() with { ProvinceCode = null, WardCode = null }, phoneLocked: false, Today);
        var (_, noProvince) = CustomerEntries.From(Organization() with { ProvinceCode = null, WardCode = null, ProvinceName = null }, phoneLocked: false, Today);

        Assert.Equal(["Invoice email"], noEmail);
        Assert.Empty(oldText);
        Assert.Null(entries.ProvinceCode);
        Assert.Equal(["Province"], noProvince);
    }

    [Fact]
    public void An_individual_with_a_personal_tax_number_is_accepted_and_a_wrong_one_is_named()
    {
        var individual = Organization() with { InvoiceBuyerType = InvoiceBuyerType.Individual, InvoiceName = "Nguyễn Văn An", TaxCode = null, InvoiceEmail = null, PersonalTaxId = "012345678901" };

        var (entries, invalid) = CustomerEntries.From(individual, phoneLocked: false, Today);
        var (_, wrong) = CustomerEntries.From(individual with { PersonalTaxId = "123" }, phoneLocked: false, Today);

        Assert.Empty(invalid);
        Assert.Equal("012345678901", entries.Invoice.PersonalTaxId);
        Assert.Equal(["Personal tax ID"], wrong);
    }

    [Fact]
    public void An_invoice_address_of_its_own_is_picked_and_the_contact_address_may_stay_text()
    {
        var request = Organization() with
        {
            ProvinceCode = null, WardCode = null, InvoiceAddressSameAsContact = false,
            InvoiceProvinceCode = 79, InvoiceProvinceName = "Thành phố Hồ Chí Minh", InvoiceWardCode = 300, InvoiceWardName = "Phường Bến Nghé", InvoiceStreet = "12 Lê Lợi",
        };

        var (entries, invalid) = CustomerEntries.From(request, phoneLocked: false, Today);

        Assert.Empty(invalid);
        Assert.False(entries.Invoice.AddressSameAsContact);
        Assert.Equal((79, 300, "12 Lê Lợi"), (entries.Invoice.Address.ProvinceCode, entries.Invoice.Address.WardCode, entries.Invoice.Address.Street));
    }

    [Fact]
    public void The_identity_number_field_is_gone_and_the_buyer_type_must_be_defined()
    {
        var (_, invalid) = CustomerEntries.From(Organization() with { InvoiceBuyerType = (InvoiceBuyerType)42 }, phoneLocked: false, Today);

        Assert.Equal(["Invoice buyer type"], invalid);
        Assert.DoesNotContain(typeof(SaveCustomerRequest).GetProperties(), p => p.Name == "IdentityNumber");
        Assert.DoesNotContain(typeof(CustomerView).GetProperties(), p => p.Name == "IdentityNumber");
    }
}
