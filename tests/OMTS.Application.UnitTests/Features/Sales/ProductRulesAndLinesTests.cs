using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Common;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The product code and unit rules, and the product lines of a harvest: the kg a cage holds, the sums that become the lot's figures and the checks of the form.</summary>
public sealed class ProductRulesAndLinesTests
{
    private static Product Oyster(Guid id, decimal? cageFactor = null, string cageName = "Lồng", int status = StatusIds.Usage.InUse)
    {
        var product = new Product { Code = "SP000001", Name = "Hàu sữa " + id.ToString()[..4], StatusId = status };
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(product, id);
        if (cageFactor is { } factor)
        {
            product.Units.Add(new ProductUnit { Unit = new InputUnit { Name = cageName }, Factor = factor, Code = "SP000001-L" });
        }

        return product;
    }

    private static HarvestLotProductLineService Service(params Product[] products) => ServiceOn(new InMemoryProductStock(), onInvoice: false, products);

    private static HarvestLotProductLineService ServiceOn(InMemoryProductStock stock, bool onInvoice, params Product[] products) =>
        new(StubProxy.Create<IProductRepository>(h => h["GetByIdsAsync"] = a => products.Where(p => ((IReadOnlyCollection<Guid>)a[0]!).Contains(p.Id)).ToList()),
            StubProxy.Create<IHarvestLotRepository>(h =>
            {
                h["AddProductLine"] = _ => null;
                h["RemoveProductLine"] = _ => null;
            }),
            StubProxy.Create<IHarvestLotSalesQuery>(h => h["IsOnAnyInvoiceAsync"] = _ => onInvoice),
            stock,
            new ProductStockLedger(stock, new Fakes.FakeCurrentUser(null), TimeProvider.System));

    [Theory]
    [InlineData(1, "SP000001")]
    [InlineData(123, "SP000123")]
    [InlineData(1000000, "SP1000000")]
    public void A_product_code_is_SP_and_at_least_six_digits(long number, string expected) => Assert.Equal(expected, ProductRules.Format(number));

    [Theory]
    [InlineData("SP000001", "Lồng", "SP000001-L")]
    [InlineData("SP000001", "Thùng", "SP000001-T")]
    [InlineData("SP000001", "kg", "SP000001-K")]
    [InlineData("SP000001", "  đôi", "SP000001-D")]
    public void A_unit_code_is_the_product_code_and_the_letter_of_the_unit(string code, string unit, string expected) =>
        Assert.Equal(expected, ProductRules.DefaultUnitCode(code, unit));

    [Theory]
    [InlineData(25, 60000, 1500000)]
    [InlineData(10.5, 60000, 630000)]
    [InlineData(0.5, 25001, 12501)]
    [InlineData(0.001, 100, 1)]
    public void A_new_unit_starts_at_its_factor_times_the_price_per_kg_in_whole_dong_and_at_least_one(double factor, double basePrice, double expected) =>
        Assert.Equal((decimal)expected, ProductRules.DefaultUnitPrice((decimal)factor, (decimal)basePrice));

    [Theory]
    [InlineData("kg", true)]
    [InlineData(" KG ", true)]
    [InlineData("Lồng", false)]
    [InlineData(null, false)]
    public void The_base_unit_is_kg_in_any_letter_case(string? name, bool expected) => Assert.Equal(expected, ProductRules.IsBaseUnitName(name));

    [Theory]
    [InlineData("Lồng")]
    [InlineData("LONG")]
    [InlineData("  lồng ")]
    public void A_cage_holds_the_factor_of_the_long_unit_of_the_product_whatever_its_accents_and_case(string unitName) =>
        Assert.Equal(25m, HarvestLotProductLineService.CageKg(Oyster(Guid.NewGuid(), 25m, unitName), 12m));

    [Fact]
    public void A_cage_holds_the_kg_per_cage_of_the_lot_when_the_product_has_no_long_unit() =>
        Assert.Equal(12m, HarvestLotProductLineService.CageKg(Oyster(Guid.NewGuid(), 10m, "Thùng"), 12m));

    [Fact]
    public async Task The_kg_of_a_line_left_empty_is_the_cages_times_the_kg_of_a_cage_and_the_lot_takes_the_sums()
    {
        var (milky, fat) = (Guid.NewGuid(), Guid.NewGuid());
        var service = Service(Oyster(milky, 25m), Oyster(fat));

        var (lines, error) = await service.ResolveAsync(
            [new HarvestProductLineInput(milky, 5, null), new HarvestProductLineInput(fat, 3, null)], unreadable: false, kgPerCage: 12.5m, new HashSet<Guid>(), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal([125m, 37.5m], lines!.Select(l => l.Kg));
        Assert.Equal((8, 162.5m), HarvestLotProductLineService.Totals(lines!));
    }

    [Fact]
    public async Task A_kg_that_was_typed_wins_over_the_cages()
    {
        var milky = Guid.NewGuid();

        var (lines, _) = await Service(Oyster(milky, 25m)).ResolveAsync([new HarvestProductLineInput(milky, 5, 100m)], false, 12.5m, new HashSet<Guid>(), CancellationToken.None);

        Assert.Equal(100m, lines!.Single().Kg);
    }

    [Fact]
    public async Task A_form_with_no_lines_is_the_old_way_of_recording_and_resolves_to_none()
    {
        var (lines, error) = await Service().ResolveAsync(null, false, 12.5m, new HashSet<Guid>(), CancellationToken.None);

        Assert.Null(lines);
        Assert.Null(error);
    }

    [Fact]
    public async Task Every_invalid_line_is_named_a_product_twice_a_product_out_of_use_cages_or_kg_not_above_zero()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var service = Service(Oyster(a, 25m), Oyster(b, 25m, status: StatusIds.Usage.Discontinued), Oyster(c, 25m), Oyster(d, 25m));

        var (lines, error) = await service.ResolveAsync(
            [
                new HarvestProductLineInput(a, 2, null),
                new HarvestProductLineInput(a, 2, null),
                new HarvestProductLineInput(b, 2, null),
                new HarvestProductLineInput(Guid.NewGuid(), 2, null),
                new HarvestProductLineInput(c, 0, null),
                new HarvestProductLineInput(d, 1, -5m),
            ],
            false, 12.5m, new HashSet<Guid>(), CancellationToken.None);

        Assert.Null(lines);
        Assert.Equal(["Line 2 product", "Line 3 product", "Line 4 product", "Line 5 cages", "Line 6 kg"], (IEnumerable<string>)error!.Details!["fields"]!);
    }

    [Fact]
    public async Task A_product_out_of_use_stays_allowed_when_the_lot_already_has_it()
    {
        var gone = Guid.NewGuid();

        var (lines, error) = await Service(Oyster(gone, 25m, status: StatusIds.Usage.Discontinued))
            .ResolveAsync([new HarvestProductLineInput(gone, 2, null)], false, 12.5m, new HashSet<Guid> { gone }, CancellationToken.None);

        Assert.Null(error);
        Assert.Single(lines!);
    }

    [Fact]
    public async Task A_hidden_product_stays_allowed_on_a_lot_that_already_has_it_but_is_refused_on_a_new_line()
    {
        var gone = Guid.NewGuid();
        var hidden = Oyster(gone, 25m);
        hidden.IsHidden = true;
        var service = Service(hidden);

        var (_, onLot) = await service.ResolveAsync([new HarvestProductLineInput(gone, 2, null)], false, 12.5m, new HashSet<Guid> { gone }, CancellationToken.None);
        var (_, fresh) = await service.ResolveAsync([new HarvestProductLineInput(gone, 2, null)], false, 12.5m, new HashSet<Guid>(), CancellationToken.None);

        Assert.Null(onLot);
        Assert.Equal("Line 1 product", fresh!.Details!["field"]);
    }

    [Fact]
    public async Task Lines_that_differ_only_in_the_scale_of_the_kg_change_nothing_and_write_no_ledger_row()
    {
        var milky = Guid.NewGuid();
        var stock = new InMemoryProductStock();
        var product = Oyster(milky, 25m);
        var lot = new HarvestLot
        {
            LotCode = "KV01-B01-20261112", HarvestedAt = DateTimeOffset.UtcNow,
            ProductLines = [new HarvestLotProductLine { LineNumber = 1, ProductId = milky, CageCount = 4, Kg = 100.000m }],
        };

        var changed = await ServiceOn(stock, false, product).ReplaceAsync(lot, "B01", [new ResolvedProductLine(product, 4, 100m)], CancellationToken.None);

        Assert.False(changed);
        Assert.Empty(stock.Rows);
    }

    [Fact]
    public async Task A_changed_kg_reverses_the_rows_of_the_lot_and_writes_the_new_ones()
    {
        var milky = Guid.NewGuid();
        var stock = new InMemoryProductStock();
        var product = Oyster(milky, 25m);
        var lot = new HarvestLot { LotCode = "KV01-B01-20261112", HarvestedAt = DateTimeOffset.UtcNow };
        var service = ServiceOn(stock, false, product);
        service.Add(lot, "B01", [new ResolvedProductLine(product, 4, 100m)]);

        var changed = await service.ReplaceAsync(lot, "B01", [new ResolvedProductLine(product, 3, 75m)], CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(75m, stock.Rows.Sum(r => r.QuantityKg));
    }

    [Fact]
    public async Task Lines_cannot_be_added_to_a_lot_that_already_has_invoice_lines()
    {
        var product = Oyster(Guid.NewGuid(), 25m);
        var lot = new HarvestLot { LotCode = "KV01-B01-20261112" };

        var error = await ServiceOn(new InMemoryProductStock(), onInvoice: true, product).GuardAsync(lot, [new ResolvedProductLine(product, 1, 25m)], CancellationToken.None);

        Assert.Equal("Product lines", error!.Details!["field"]);
    }

    [Fact]
    public async Task A_product_with_sales_on_the_lot_cannot_be_taken_off_it_but_one_with_none_can()
    {
        var (sold, unsold) = (Guid.NewGuid(), Guid.NewGuid());
        var stock = new InMemoryProductStock();
        var lot = new HarvestLot
        {
            LotCode = "KV01-B01-20261112",
            ProductLines = [new HarvestLotProductLine { LineNumber = 1, ProductId = sold, CageCount = 1, Kg = 10 }, new HarvestLotProductLine { LineNumber = 2, ProductId = unsold, CageCount = 1, Kg = 10 }],
        };
        stock.Add(new ProductStockMovement { ProductId = sold, HarvestLotId = lot.Id, Kind = ProductStockMovementKind.Sale, QuantityKg = -5m });
        var keepsSold = new ResolvedProductLine(Oyster(sold, 25m), 1, 10m);
        var keepsUnsold = new ResolvedProductLine(Oyster(unsold, 25m), 1, 10m);
        var service = ServiceOn(stock, false);

        Assert.NotNull(await service.GuardAsync(lot, [keepsUnsold], CancellationToken.None));
        Assert.Null(await service.GuardAsync(lot, [keepsSold], CancellationToken.None));
    }

    [Fact]
    public async Task A_line_value_that_could_not_be_read_or_no_line_at_all_is_refused()
    {
        var service = Service();

        Assert.NotNull((await service.ResolveAsync([new HarvestProductLineInput(null, 1, 1)], unreadable: true, 12.5m, new HashSet<Guid>(), CancellationToken.None)).Error);
        Assert.NotNull((await service.ResolveAsync([], false, 12.5m, new HashSet<Guid>(), CancellationToken.None)).Error);
    }
}
