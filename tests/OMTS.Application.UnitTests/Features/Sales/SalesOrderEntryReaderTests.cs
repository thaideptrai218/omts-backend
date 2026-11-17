using System.Text.Json;
using System.Text.Json.Serialization;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The shape of an order as entered: MSG-GEN-01 names every field to fix, a line has no gap, quantities have at most 3 decimals, amounts are whole VND, a walk-in buyer pays no advance.</summary>
public class SalesOrderEntryReaderTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Unit = Guid.NewGuid();

    private static SalesOrderLineInput Line(decimal? quantity = 10m, decimal? price = null) => new(Product, Unit, quantity, price);

    private static SaveSalesOrderRequest Request(
        IReadOnlyList<SalesOrderLineInput?>? lines = null, Guid? customer = null, bool? walkIn = null, SalesOrderSource? source = SalesOrderSource.Phone, string? code = null,
        decimal discount = 0m, SalesOrderAdvanceInput? advance = null, Guid? purchaseRequest = null, string? rowVersion = null) =>
        new(code, customer ?? Customer, walkIn ?? false, purchaseRequest, source, null, null, null, null, discount, false, lines ?? [Line()], advance, null, rowVersion);

    private static List<string> Invalid(SaveSalesOrderRequest request, bool isUpdate = false)
    {
        var (entries, invalid) = SalesOrderEntryReader.Read(request, isUpdate);
        Assert.Null(entries);
        return invalid;
    }

    [Fact]
    public void A_valid_order_is_read_with_its_lines_numbered_from_one()
    {
        var (entries, invalid) = SalesOrderEntryReader.Read(Request([Line(10m, 90_000m), Line(2.5m)], code: " dh-77 "), isUpdate: false);

        Assert.Empty(invalid);
        Assert.Equal("dh-77", entries!.Code);
        Assert.Equal([1, 2], entries.Lines.Select(l => l.LineNumber));
        Assert.Equal([90_000m, null], entries.Lines.Select(l => l.UnitPrice));
        Assert.Equal(SalesOrderSource.Phone, entries.Source);
        Assert.False(entries.IsWalkIn);
    }

    [Fact]
    public void An_empty_code_stays_empty_so_the_next_one_is_given() =>
        Assert.Null(SalesOrderEntryReader.Read(Request(code: "  "), isUpdate: false).Entries!.Code);

    [Theory]
    [InlineData("bad code")]
    [InlineData("mã-1")]
    [InlineData("0123456789012345678901234567890")]
    public void A_code_the_user_types_has_letters_digits_and_a_few_signs_only(string code) => Assert.Contains("Code", Invalid(Request(code: code)));

    [Fact]
    public void An_order_has_no_zone_and_one_sent_is_ignored()
    {
        var request = JsonSerializer.Deserialize<SaveSalesOrderRequest>(
            $$"""{"zoneId":"{{Guid.NewGuid()}}","customerId":"{{Customer}}","walkInCustomer":false,"source":"Phone","discount":0,"lines":[{"productId":"{{Product}}","unitId":"{{Unit}}","quantity":1}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

        var (entries, invalid) = SalesOrderEntryReader.Read(request!, isUpdate: false);

        Assert.Empty(invalid);
        Assert.Equal(Customer, entries!.CustomerId);
    }

    [Theory]
    [MemberData(nameof(BrokenLines))]
    public void A_line_with_a_gap_names_its_field(IReadOnlyList<SalesOrderLineInput?>? lines, string field) => Assert.Contains(field, Invalid(Request() with { Lines = lines }));

    public static TheoryData<IReadOnlyList<SalesOrderLineInput?>?, string> BrokenLines() => new()
    {
        { null, "Lines" },
        { [], "Lines" },
        { new SalesOrderLineInput?[] { null }, "Line 1 product" },
        { new[] { new SalesOrderLineInput(null, Unit, 1m) }, "Line 1 product" },
        { new[] { new SalesOrderLineInput(Product, null, 1m) }, "Line 1 unit" },
        { new[] { new SalesOrderLineInput(Product, Unit, null) }, "Line 1 quantity" },
        { new[] { new SalesOrderLineInput(Product, Unit, 0m) }, "Line 1 quantity" },
        { new[] { new SalesOrderLineInput(Product, Unit, -2m) }, "Line 1 quantity" },
        { new[] { new SalesOrderLineInput(Product, Unit, 1.2345m) }, "Line 1 quantity" },
        { new[] { Line(), new SalesOrderLineInput(Product, Unit, 1m, 0m) }, "Line 2 unit price" },
        { new[] { Line(), Line(), new SalesOrderLineInput(Product, Unit, 1m, 100.5m) }, "Line 3 unit price" },
        { new[] { new SalesOrderLineInput(Product, Unit, 1m, 1m, Note: new string('x', 501)) }, "Line 1 note" },
    };

    [Fact]
    public void Every_broken_field_is_named_at_once()
    {
        var invalid = Invalid(Request(new[] { new SalesOrderLineInput(null, null, 0m) }, code: "bad code", discount: 0.5m));

        Assert.Equal(["Code", "Discount", "Line 1 product", "Line 1 quantity", "Line 1 unit"], invalid.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void More_lines_than_the_limit_are_refused() =>
        Assert.Contains("Lines", Invalid(Request(Enumerable.Repeat<SalesOrderLineInput?>(Line(), SalesOrderRules.MaxLines + 1).ToList())));

    [Fact]
    public void A_customer_is_chosen_or_the_buyer_is_a_walk_in_never_assumed()
    {
        Assert.Contains("Customer", Invalid(Request(walkIn: true)));
        var noProfile = Request() with { CustomerId = null, WalkInCustomer = false };
        Assert.Contains("Customer", Invalid(noProfile));

        var (entries, _) = SalesOrderEntryReader.Read(Request() with { CustomerId = null, WalkInCustomer = true }, isUpdate: false);
        Assert.True(entries!.IsWalkIn);
    }

    [Fact]
    public void The_source_is_required_unless_a_purchase_request_makes_it_app()
    {
        Assert.Contains("Source", Invalid(Request(source: null)));
        Assert.Contains("Source", Invalid(Request(source: (SalesOrderSource)99)));

        var (entries, invalid) = SalesOrderEntryReader.Read(Request(source: null, purchaseRequest: Guid.NewGuid()), isUpdate: false);
        Assert.Empty(invalid);
        Assert.Equal(SalesOrderSource.App, entries!.Source);
        Assert.Contains("Source", Invalid(Request(source: SalesOrderSource.Phone, purchaseRequest: Guid.NewGuid())));
    }

    [Fact]
    public void A_purchase_request_belongs_to_a_customer_and_never_changes_on_an_update()
    {
        Assert.Contains("Purchase request", Invalid(Request(customer: null, walkIn: true, purchaseRequest: Guid.NewGuid()) with { CustomerId = null }));
        Assert.Contains("Purchase request", Invalid(Request(purchaseRequest: Guid.NewGuid(), rowVersion: "v"), isUpdate: true));
    }

    [Fact]
    public void An_update_needs_the_row_version_it_read()
    {
        Assert.Contains("RowVersion", Invalid(Request(), isUpdate: true));
        Assert.Empty(SalesOrderEntryReader.Read(Request(rowVersion: "AAAB"), isUpdate: true).Invalid);
    }

    // ---- The advance ----

    private static SalesOrderAdvanceInput Advance(decimal? amount = 500_000m, SalesOrderAdvanceMethod? method = SalesOrderAdvanceMethod.Cash, Guid? account = null) =>
        new(amount, method, account);

    [Fact]
    public void A_valid_advance_is_read()
    {
        var (entries, invalid) = SalesOrderEntryReader.Read(Request(advance: Advance()), isUpdate: false);

        Assert.Empty(invalid);
        Assert.Equal((500_000m, SalesOrderAdvanceMethod.Cash), (entries!.Advance!.Amount, entries.Advance.Method));
    }

    [Fact]
    public void A_walk_in_buyer_pays_no_advance()
    {
        var request = Request(advance: Advance()) with { CustomerId = null, WalkInCustomer = true };

        Assert.Equal(["Advance"], Invalid(request));
    }

    [Fact]
    public void An_update_takes_a_new_advance_in_the_same_save()
    {
        var (entries, invalid) = SalesOrderEntryReader.Read(Request(advance: Advance(), rowVersion: "AAAB"), isUpdate: true);

        Assert.Empty(invalid);
        Assert.Equal((500_000m, SalesOrderAdvanceMethod.Cash), (entries!.Advance!.Amount, entries.Advance.Method));
    }

    [Fact]
    public void An_update_of_a_walk_in_order_takes_no_advance()
    {
        var request = Request(advance: Advance(), rowVersion: "AAAB") with { CustomerId = null, WalkInCustomer = true };

        Assert.Equal(["Advance"], Invalid(request, isUpdate: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000.5)]
    public void An_advance_is_whole_dong_above_zero(double amount) => Assert.Contains("Advance amount", Invalid(Request(advance: Advance((decimal)amount))));

    [Fact]
    public void An_advance_has_a_method_and_a_vietqr_one_names_the_bank_account()
    {
        Assert.Contains("Advance amount", Invalid(Request(advance: Advance(null))));
        Assert.Contains("Advance method", Invalid(Request(advance: Advance(method: null))));
        Assert.Contains("Cash account", Invalid(Request(advance: Advance(method: SalesOrderAdvanceMethod.VietQr))));
        Assert.Empty(SalesOrderEntryReader.Read(Request(advance: Advance(method: SalesOrderAdvanceMethod.VietQr, account: Guid.NewGuid())), isUpdate: false).Invalid);
    }
}
