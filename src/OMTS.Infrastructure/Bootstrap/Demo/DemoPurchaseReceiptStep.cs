using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// PURCHASE-01/02/05/06: the demo receipts of <see cref="DemoReceiptPlans"/>, saved through the receipt, cancellation,
/// return and payment services with their real dates (the services accept past receipt and payment dates; a return is dated
/// by hand, see <see cref="ReturnAsync"/>), so totals, supplier debts and raft material costs are computed by the normal rules. The Owner is notified
/// of each completed receipt (MSG-PURCHASE-01) and each payment (MSG-PURCHASE-04). Each receipt is found by its plan code at
/// the start of its notes; every follow-up (payment, return, cancel) is checked against the stored state or carries a
/// deduplication key, so an interrupted run is completed by the next one. Two extra returns show the other paths: a Draft of a
/// receipt, and a return without a receipt with a discount and money paid back.
/// </summary>
internal sealed class DemoPurchaseReceiptStep : IDemoSeedStep
{
    public string Name => "purchase receipts";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var receipts = context.Get<PurchaseReceiptService>();
        var created = 0;
        foreach (var plan in DemoReceiptPlans.All)
        {
            var supplierPhone = PhoneNumbers.Normalize(plan.SupplierPhone);
            var supplierId = await db.Suppliers.Where(s => s.PhoneNumber == supplierPhone).Select(s => s.Id).SingleAsync(cancellationToken);
            var prefix = $"{plan.Code} ";
            var receiptId = await db.PurchaseReceipts.Where(r => r.Notes != null && r.Notes.StartsWith(prefix)).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
            if (receiptId is null)
            {
                receiptId = DemoSeedContext.Ensure(
                    await receipts.CreateAsync(await RequestAsync(context, plan, supplierId, prefix, cancellationToken), cancellationToken), $"receipt {plan.Code}").Id;
                created++;
            }

            await FollowUpAsync(context, plan, receiptId.Value, supplierId, cancellationToken);
        }

        await SupplierTransactionsAsync(context, cancellationToken);
        await ExtraReturnsAsync(context, cancellationToken);
        return created;
    }

    private static async Task<SavePurchaseReceiptRequest> RequestAsync(
        DemoSeedContext context, DemoReceiptPlan plan, Guid supplierId, string prefix, CancellationToken cancellationToken)
    {
        var db = context.Db;
        Guid? raftId = null;
        if (plan is { RaftZone: { } raftZone, RaftNumber: { } raftNumber })
        {
            var raftCode = DemoRaftStep.RaftCode(raftZone, raftNumber);
            raftId = await db.Rafts.Where(r => r.Zone.Code == raftZone && r.Code == raftCode).Select(r => r.Id).SingleAsync(cancellationToken);
        }

        // A receipt not bought for a raft is still booked to a zone: the first demo zone.
        var zoneCode = plan.RaftZone ?? DemoFarmingZoneStep.ZoneOne;
        var zoneId = await db.FarmingZones.Where(z => z.Code == zoneCode).Select(z => z.Id).SingleAsync(cancellationToken);
        var itemCodes = plan.Lines.Select(l => l.Item).ToList();
        var items = await db.FarmingInputItems.Where(i => itemCodes.Contains(i.Code)).ToDictionaryAsync(i => i.Code, cancellationToken);
        // The raft goes on the Seed and Material lines only (GBR-04, BR-FARM-05).
        return new SavePurchaseReceiptRequest(
            supplierId, context.Today.AddDays(-plan.DaysAgo), $"{prefix}{plan.Note}",
            plan.Lines.Select(l => new ReceiptLineInput(
                items[l.Item].Id, l.Quantity, l.UnitPrice, l.SupplierLotCode,
                items[l.Item].Group is InputItemGroup.Seed or InputItemGroup.Material ? raftId : null, l.Discount)).ToList(),
            Complete: plan.Outcome != DemoReceiptOutcome.Draft, Discount: plan.Discount, ZoneId: zoneId);
    }

    /// <summary>Payments, the return and the cancellation of a receipt, each only when it is not already stored.</summary>
    private static async Task FollowUpAsync(DemoSeedContext context, DemoReceiptPlan plan, Guid receiptId, Guid supplierId, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var statusId = await db.PurchaseReceipts.Where(r => r.Id == receiptId).Select(r => r.StatusId).SingleAsync(cancellationToken);
        if (statusId != StatusIds.PurchaseReceipt.Completed)
        {
            return;
        }

        var payments = context.Get<SupplierPaymentService>();
        for (var i = 0; i < (plan.Payments?.Length ?? 0); i++)
        {
            var payment = plan.Payments![i];
            var key = $"{plan.Code}-PAY{i + 1}";
            // The demo payments name no receipt: the plan only says when each was made (BR-PURCHASE-04). The demo answers Yes
            // to the advance warning, because a supplier's later receipts use up what it was paid first (BR-PURCHASE-08).
            DemoSeedContext.Ensure(await payments.RecordAsync(new RecordSupplierPaymentRequest(
                supplierId, SupplierPaymentKind.Payment, context.Today.AddDays(-payment.DaysAgo), payment.Amount, payment.Method,
                payment.Method == PaymentMethod.BankTransfer ? $"FT-{key}" : null, ConfirmAdvance: true, DeduplicationKey: key), cancellationToken),
                $"payment {i + 1} of {plan.Code}");
        }

        if (plan.Return is { } planned)
        {
            await ReturnAsync(context, plan.Code, planned, receiptId, cancellationToken);
        }

        if (plan.Outcome == DemoReceiptOutcome.Cancelled)
        {
            DemoSeedContext.Ensure(await context.Get<PurchaseReceiptCancellationService>().CancelAsync(
                receiptId, new CancelReceiptRequest(plan.CancelReason), cancellationToken), $"cancelling {plan.Code}");
        }
    }

    /// <summary>
    /// The return of a receipt, then its cancellation by the Owner when planned. The service dates a return today (BR-PURCHASE-07);
    /// the demo moves it into the history it builds, the only place a return date is ever set by hand.
    /// </summary>
    private static async Task ReturnAsync(DemoSeedContext context, string code, DemoReturn planned, Guid receiptId, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var returns = context.Get<PurchaseReturnService>();
        var stored = await db.PurchaseReturns.FirstOrDefaultAsync(r => r.ReceiptId == receiptId, cancellationToken);
        if (stored is null)
        {
            var view = DemoSeedContext.Ensure(await context.Get<PurchaseReceiptService>().GetAsync(receiptId, cancellationToken), $"reading {code}");
            // A receipt stored before the VT codes keeps the older code on its line: its item has the same name.
            var name = DemoInputItemStep.NameOf(planned.Item);
            var line = view.Lines.Single(l => l.ItemCode == planned.Item || l.ItemName == name);
            DemoSeedContext.Ensure(await returns.CreateAsync(
                receiptId, new CreatePurchaseReturnRequest(planned.Reason, [new ReturnLineInput(line.Id, planned.Quantity)]), cancellationToken), $"return of {code}");
            stored = await db.PurchaseReturns.SingleAsync(r => r.ReceiptId == receiptId, cancellationToken);
            PurchaseReturnRules.SetReturned(stored, BusinessClock.FromLocal(context.Today.AddDays(-planned.DaysAgo).ToDateTime(new TimeOnly(9, 0))));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (planned.CancelReason is { } reason && stored.StatusId == StatusIds.PurchaseReturn.Recorded)
        {
            DemoSeedContext.Ensure(await returns.CancelAsync(stored.Id, new CancelPurchaseReturnRequest(reason), cancellationToken), $"cancelling the return of {code}");
        }
    }

    /// <summary>A Draft return of a receipt, and a return with no receipt dated in the history, each found by the marker its reason starts with.</summary>
    private static async Task ExtraReturnsAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        const string DraftMarker = "DEMO-RETURN-DRAFT ";
        const string StandaloneMarker = "DEMO-RETURN-ALONE ";
        var db = context.Db;
        var returns = context.Get<PurchaseReturnService>();
        var zoneId = await db.FarmingZones.Where(z => z.Code == DemoFarmingZoneStep.ZoneOne).Select(z => z.Id).SingleAsync(cancellationToken);
        if (!await db.PurchaseReturns.AnyAsync(r => r.Reason.StartsWith(DraftMarker), cancellationToken))
        {
            var receiptId = await db.PurchaseReceipts.Where(r => r.Notes != null && r.Notes.StartsWith("DEMO-R15 ")).Select(r => r.Id).SingleAsync(cancellationToken);
            var view = DemoSeedContext.Ensure(await context.Get<PurchaseReceiptService>().GetAsync(receiptId, cancellationToken), "reading the receipt of the draft return");
            DemoSeedContext.Ensure(await returns.CreateAsync(new SavePurchaseReturnRequest(
                receiptId, null, zoneId, $"{DraftMarker}Dầu lẫn nước, chờ nhà cung cấp xác nhận",
                Lines: [new PurchaseReturnLineInput(view.Lines[0].Id, null, null, 10)]), cancellationToken), "the draft return");
        }

        if (!await db.PurchaseReturns.AnyAsync(r => r.Reason.StartsWith(StandaloneMarker), cancellationToken))
        {
            var shop = PhoneNumbers.Normalize(DemoSupplierStep.MaterialShopPhone);
            var supplierId = await db.Suppliers.Where(s => s.PhoneNumber == shop).Select(s => s.Id).SingleAsync(cancellationToken);
            var itemId = await db.FarmingInputItems.Where(i => i.Code == "VT009").Select(i => i.Id).SingleAsync(cancellationToken);
            var saved = DemoSeedContext.Ensure(await returns.CreateAsync(new SavePurchaseReturnRequest(
                null, supplierId, zoneId, $"{StandaloneMarker}Cọc neo mua lẻ bị nứt, trả lại không theo phiếu nhập", 100_000, Complete: true,
                Refund: new ReturnRefundInput(300_000, PaymentMethod.Cash),
                Lines: [new PurchaseReturnLineInput(null, itemId, 450_000, 2)]), cancellationToken), "the return without a receipt");
            var stored = await db.PurchaseReturns.SingleAsync(r => r.Id == saved.Id, cancellationToken);
            PurchaseReturnRules.SetReturned(stored, BusinessClock.FromLocal(context.Today.AddDays(-6).ToDateTime(new TimeOnly(9, 0))));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>PURCHASE-05.2 and 05.3: money a supplier paid back after a return, and a payment recorded by mistake then cancelled.</summary>
    private static async Task SupplierTransactionsAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var payments = context.Get<SupplierPaymentService>();
        var company = PhoneNumbers.Normalize(DemoSupplierStep.MaterialCompanyPhone);
        var supplierId = await db.Suppliers.Where(s => s.PhoneNumber == company).Select(s => s.Id).SingleAsync(cancellationToken);
        DemoSeedContext.Ensure(await payments.RecordAsync(new RecordSupplierPaymentRequest(
            supplierId, SupplierPaymentKind.PaidBack, context.Today.AddDays(-15), 500_000, PaymentMethod.Cash,
            Note: "Hoàn tiền phần lưới rách đã trả lại", DeduplicationKey: "DEMO-PAIDBACK-1"), cancellationToken), "money paid back");

        var mistake = DemoSeedContext.Ensure(await payments.RecordAsync(new RecordSupplierPaymentRequest(
            supplierId, SupplierPaymentKind.Payment, context.Today.AddDays(-9), 1_250_000, PaymentMethod.BankTransfer,
            "FT-DEMO-NHAM-1", "Chuyển nhầm số tiền", ConfirmAdvance: true, DeduplicationKey: "DEMO-MISTAKE-1"), cancellationToken), "the mistaken payment");
        if (mistake.StatusId == StatusIds.SupplierPayment.Recorded)
        {
            DemoSeedContext.Ensure(await payments.CancelAsync(
                mistake.Id, new CancelSupplierPaymentRequest("Ghi nhầm số tiền, đã ghi lại"), cancellationToken), "cancelling the mistaken payment");
        }
    }
}
