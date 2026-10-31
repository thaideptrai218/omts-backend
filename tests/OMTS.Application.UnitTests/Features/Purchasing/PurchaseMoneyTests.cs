using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

public class PurchaseMoneyTests
{
    [Theory]
    [InlineData(1.5, 333, 0)]
    [InlineData(3, 499, 1000)]
    [InlineData(2.5, 1300, 3000)]
    [InlineData(12.345, 85000, 1049000)]
    public void Line_amount_is_rounded_to_the_nearest_thousand(decimal quantity, decimal unitPrice, decimal expected) =>
        Assert.Equal(expected, PurchaseMoney.LineAmount(quantity, unitPrice));

    [Fact]
    public void Return_value_carries_the_discount_share()
    {
        // Subtotal 1,000,000, discount 100,000: 10 of 100 units at 10,000 return 90,000.
        Assert.Equal(90_000, PurchaseMoney.ReturnValue(10, 10_000, payable: 900_000, subtotal: 1_000_000));
        Assert.Equal(0, PurchaseMoney.ReturnValue(1, 10_000, payable: 0, subtotal: 0));
    }

    [Fact]
    public void Outstanding_is_net_payable_minus_net_paid_and_cancelled_payments_do_not_count()
    {
        var receipt = Receipt(discount: 100_000, (InputItemGroup.Seed, 100, 10_000));
        receipt.Payments.Add(new SupplierPayment { Amount = 300_000, StatusId = StatusIds.SupplierPayment.Recorded });
        receipt.Payments.Add(new SupplierPayment { Amount = 50_000, StatusId = StatusIds.SupplierPayment.Cancelled });
        var line = receipt.Lines.First();
        receipt.Returns.Add(new PurchaseReturn
        {
            RefundAmount = 20_000,
            DebtReductionAmount = 70_000,
            Lines = [new PurchaseReturnLine { ReceiptLineId = line.Id, Quantity = 10, ReturnValue = 90_000 }],
        });

        var figures = PurchaseMoney.Of(receipt);

        Assert.Equal(1_000_000, figures.Subtotal);
        Assert.Equal(900_000, figures.Payable);
        Assert.Equal(90_000, figures.Returned);
        Assert.Equal(280_000, figures.NetPaid);
        Assert.Equal(900_000 - 90_000 - 280_000, figures.Outstanding);
    }

    [Fact]
    public void Raft_material_cost_counts_material_lines_less_discount_share_and_returns_on_completed_receipts()
    {
        // Floats 600,000 + seed 400,000, discount 100,000 (10 %): floats cost 540,000, less 54,000 returned.
        var receipt = Receipt(discount: 100_000, (InputItemGroup.Material, 60, 10_000), (InputItemGroup.Seed, 40, 10_000));
        var floats = receipt.Lines.First(l => l.Item.Group == InputItemGroup.Material);
        receipt.Returns.Add(new PurchaseReturn { Lines = [new PurchaseReturnLine { ReceiptLineId = floats.Id, Quantity = 6, ReturnValue = 54_000 }] });

        Assert.Equal(486_000, PurchaseMoney.RaftMaterialCost(receipt));

        receipt.StatusId = StatusIds.PurchaseReceipt.Cancelled;
        Assert.Equal(0, PurchaseMoney.RaftMaterialCost(receipt));
    }

    [Fact]
    public void Supplier_debt_subtracts_unallocated_payments()
    {
        var receipt = Receipt(discount: 0, (InputItemGroup.Other, 1, 500_000));
        Assert.Equal(300_000, SupplierDebts.Debt([receipt], unallocated: 200_000));
    }

    private static PurchaseReceipt Receipt(decimal discount, params (InputItemGroup Group, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var receipt = new PurchaseReceipt { DiscountAmount = discount, StatusId = StatusIds.PurchaseReceipt.Completed };
        foreach (var (group, quantity, unitPrice) in lines)
        {
            receipt.Lines.Add(new PurchaseReceiptLine
            {
                Item = new FarmingInputItem { Group = group },
                Quantity = quantity,
                UnitPrice = unitPrice,
                LineAmount = PurchaseMoney.LineAmount(quantity, unitPrice),
            });
        }

        return receipt;
    }
}
