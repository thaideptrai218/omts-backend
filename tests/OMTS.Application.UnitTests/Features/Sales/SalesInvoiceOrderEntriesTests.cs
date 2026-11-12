using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The shape of an invoice made from an order, and what its lines resolve to: the unit, the quantity in it and the order line.</summary>
public class SalesInvoiceOrderEntriesTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Lot = Guid.NewGuid();

    private static CreateSalesInvoiceRequest Request(
        Guid? orderId, string? code = null, SalesInvoiceRestPayment rest = SalesInvoiceRestPayment.None, Guid? purchaseRequestId = null, bool walkIn = false, Guid? customerId = null) =>
        new(customerId, walkIn, null, [new SalesInvoiceLineEntry(Product, Lot, 10m)], new SalesInvoicePaymentEntry(0m, rest), PurchaseRequestId: purchaseRequestId, SalesOrderId: orderId, Code: code);

    [Fact]
    public void A_new_invoice_needs_its_order_and_the_customer_may_be_left_to_the_order()
    {
        var (_, invalid) = SalesInvoiceEntryReader.Read(Request(orderId: null));
        var (entries, none) = SalesInvoiceEntryReader.Read(Request(Guid.NewGuid()));

        Assert.Equal(["Sales order"], invalid);
        Assert.Empty(none);
        Assert.Null(entries!.CustomerId);
    }

    [Fact]
    public void A_sale_that_was_awaiting_its_transfer_before_orders_is_read_without_one()
    {
        var (entries, invalid) = SalesInvoiceEntryReader.Read(Request(orderId: null, customerId: Guid.NewGuid()), requireOrder: false);

        Assert.Empty(invalid);
        Assert.Null(entries!.SalesOrderId);
    }

    [Fact]
    public void The_request_link_of_the_old_flow_is_refused_for_an_invoice_of_an_order()
    {
        var (_, invalid) = SalesInvoiceEntryReader.Read(Request(Guid.NewGuid(), purchaseRequestId: Guid.NewGuid(), customerId: Guid.NewGuid()));

        Assert.Equal(["Purchase request"], invalid);
    }

    [Theory]
    [InlineData("HD-1", SalesInvoiceRestPayment.None, true)]
    [InlineData("a b", SalesInvoiceRestPayment.None, false)]
    [InlineData("", SalesInvoiceRestPayment.None, true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", SalesInvoiceRestPayment.None, true)] // 26 characters
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", SalesInvoiceRestPayment.VietQr, false)] // the transfer content holds 25
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXY", SalesInvoiceRestPayment.VietQr, true)]
    public void A_typed_code_follows_the_code_rules_and_the_length_of_the_transfer_content(string code, SalesInvoiceRestPayment rest, bool valid)
    {
        var (_, invalid) = SalesInvoiceEntryReader.Read(Request(Guid.NewGuid(), code, rest));

        Assert.Equal(valid, invalid.Count == 0);
    }

    [Fact]
    public void A_note_is_cut_at_a_thousand_characters_and_a_quantity_has_at_most_three_decimals()
    {
        var long_ = new string('x', 1_001);
        var (_, invalid) = SalesInvoiceEntryReader.Read(Request(Guid.NewGuid()) with { Note = long_, Lines = [new SalesInvoiceLineEntry(Product, Lot, 10m, Quantity: 1.0001m)] });

        Assert.Equal(["Line 1 quantity", "Note"], invalid);
    }

    // ---- the unit, the quantity and the order line of each line

    private static (Product Product, ProductUnit Kg, ProductUnit Cage) Catalogue()
    {
        var product = new Product { Name = "Hàu sữa", UnitPrice = 50_000m };
        var kg = new ProductUnit { ProductId = product.Id, UnitId = Guid.NewGuid(), IsBase = true, Factor = 1m, Code = "SP1-K", SalePrice = 50_000m, Unit = new InputUnit { Name = "kg" } };
        var cage = new ProductUnit { ProductId = product.Id, UnitId = Guid.NewGuid(), IsBase = false, Factor = 20m, Code = "SP1-L", SalePrice = 1_200_000m, Unit = new InputUnit { Name = "Lồng" } };
        product.Units.Add(kg);
        product.Units.Add(cage);
        return (product, kg, cage);
    }

    private static SalesInvoiceLineEntries Line(Guid productId, decimal kg, Guid? unitId = null, decimal? quantity = null, Guid? orderLineId = null) =>
        new(1, productId, Lot, kg, null, false, orderLineId, unitId, quantity);

    [Fact]
    public void A_line_without_a_unit_counts_in_kg_and_the_kg_are_its_quantity()
    {
        var (product, kg, _) = Catalogue();

        var resolved = SalesInvoiceLineResolver.Resolve([Line(product.Id, 12.5m)], new Dictionary<Guid, Product> { [product.Id] = product }, null, []);

        var line = resolved.Value![1];
        Assert.Equal((kg.UnitId, 12.5m, true, 50_000m), (line.Unit.UnitId, line.Quantity, line.Unit.IsBase, line.Unit.SalePrice));
        Assert.Null(line.OrderLine);
    }

    [Fact]
    public void A_line_in_a_cage_needs_its_quantity_and_names_a_unit_of_its_product()
    {
        var (product, _, cage) = Catalogue();
        var products = new Dictionary<Guid, Product> { [product.Id] = product };

        var counted = SalesInvoiceLineResolver.Resolve([Line(product.Id, 38.5m, cage.UnitId, 2m)], products, null, []);
        var missing = SalesInvoiceLineResolver.Resolve([Line(product.Id, 38.5m, cage.UnitId)], products, null, []);
        var stranger = SalesInvoiceLineResolver.Resolve([Line(product.Id, 38.5m, Guid.NewGuid(), 2m)], products, null, []);

        Assert.Equal((2m, false), (counted.Value![1].Quantity, counted.Value[1].Unit.IsBase));
        Assert.Equal("MSG-GEN-01", missing.Error!.Message.Code);
        Assert.Equal("Line 1 quantity", missing.Error.Details!["field"]);
        Assert.Equal("Line 1 unit", stranger.Error!.Details!["field"]);
    }

    [Fact]
    public void An_order_line_must_be_of_the_order_the_same_product_and_the_same_unit()
    {
        var (product, kg, cage) = Catalogue();
        var order = new SalesOrder();
        var kgLine = new SalesOrderLine { ProductId = product.Id, UnitId = kg.UnitId, Quantity = 100m, UnitPrice = 50_000m };
        order.Lines.Add(kgLine);
        var products = new Dictionary<Guid, Product> { [product.Id] = product };

        var linked = SalesInvoiceLineResolver.Resolve([Line(product.Id, 10m, orderLineId: kgLine.Id)], products, order, []);
        var other = SalesInvoiceLineResolver.Resolve([Line(product.Id, 10m, orderLineId: Guid.NewGuid())], products, order, []);
        var wrongUnit = SalesInvoiceLineResolver.Resolve([Line(product.Id, 10m, cage.UnitId, 1m, kgLine.Id)], products, order, []);
        var noOrder = SalesInvoiceLineResolver.Resolve([Line(product.Id, 10m, orderLineId: kgLine.Id)], products, null, []);

        Assert.Same(kgLine, linked.Value![1].OrderLine);
        Assert.Equal("Line 1 order line", other.Error!.Details!["field"]);
        Assert.Equal("Line 1 order line", wrongUnit.Error!.Details!["field"]);
        Assert.Equal("Line 1 order line", noOrder.Error!.Details!["field"]);
    }

    [Fact]
    public void A_unit_that_left_the_product_stays_for_the_lines_that_already_count_in_it()
    {
        var (product, _, _) = Catalogue();
        var gone = Guid.NewGuid();
        var existing = new SalesInvoiceLine { ProductId = product.Id, UnitId = gone, UnitCode = "SP1-T", UnitName = "Thùng", Factor = 10m, Quantity = 2m, Kg = 20m, ListPrice = 700_000m };

        var resolved = SalesInvoiceLineResolver.Resolve([Line(product.Id, 20m, gone, 3m)], new Dictionary<Guid, Product> { [product.Id] = product }, null, [existing]);
        var refused = SalesInvoiceLineResolver.Resolve([Line(product.Id, 20m, gone, 3m)], new Dictionary<Guid, Product> { [product.Id] = product }, null, []);

        Assert.Equal(("Thùng", 700_000m, 3m), (resolved.Value![1].Unit.Name, resolved.Value[1].Unit.SalePrice, resolved.Value[1].Quantity));
        Assert.False(refused.IsSuccess);
    }
}
