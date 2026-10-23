using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// INP-03…INP-06: the twenty demo receipts of <see cref="DemoReceiptPlans"/>, saved through the receipt,
/// cancellation, return and payment services with their real dates (the services accept past receipt, payment
/// and return dates), so totals, supplier debts and raft material costs are computed by the normal rules.
/// Each receipt is found by its code at the start of its notes; every follow-up (payment, return, cancel) is
/// checked against the stored state, so an interrupted run is completed by the next one.
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

        return created;
    }

    private static async Task<SavePurchaseReceiptRequest> RequestAsync(
        DemoSeedContext context, DemoReceiptPlan plan, Guid supplierId, string prefix, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var zoneId = await db.FarmingZones.Where(z => z.Code == plan.Zone).Select(z => z.Id).SingleAsync(cancellationToken);
        Guid? raftId = null;
        if (plan.RaftNumber is { } raftNumber)
        {
            var raftCode = DemoRaftStep.RaftCode(plan.Zone, raftNumber);
            raftId = await db.Rafts.Where(r => r.ZoneId == zoneId && r.Code == raftCode).Select(r => r.Id).SingleAsync(cancellationToken);
        }

        var itemCodes = plan.Lines.Select(l => l.Item).ToList();
        var itemIds = await db.FarmingInputItems.Where(i => itemCodes.Contains(i.Code)).ToDictionaryAsync(i => i.Code, i => i.Id, cancellationToken);
        return new SavePurchaseReceiptRequest(
            supplierId, zoneId, raftId, context.Today.AddDays(-plan.DaysAgo), plan.Discount, plan.PaidNow,
            plan.PaidNow > 0 ? plan.PaidNowMethod : null, $"{prefix}{plan.Note}",
            plan.Lines.Select(l => new ReceiptLineInput(itemIds[l.Item], l.Quantity, l.UnitPrice, l.SupplierLotCode, l.ActualSource)).ToList(),
            Complete: plan.Outcome != DemoReceiptOutcome.Draft);
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
        for (var i = 0; i < (plan.Later?.Length ?? 0); i++)
        {
            var later = plan.Later![i];
            DemoSeedContext.Ensure(await payments.RecordAsync(new RecordSupplierPaymentRequest(
                supplierId, receiptId, later.Amount, context.Today.AddDays(-later.DaysAgo), later.Method, null, $"{plan.Code}-PAY{i + 1}"), cancellationToken),
                $"payment {i + 1} of {plan.Code}");
        }

        if (plan.Return is { } partialReturn && !await db.PurchaseReturns.AnyAsync(r => r.ReceiptId == receiptId, cancellationToken))
        {
            var view = DemoSeedContext.Ensure(await context.Get<PurchaseReceiptService>().GetAsync(receiptId, cancellationToken), $"reading {plan.Code}");
            var line = view.Lines.Single(l => l.ItemCode == partialReturn.Item);
            DemoSeedContext.Ensure(await context.Get<PurchaseReturnService>().CreateAsync(receiptId, new CreatePurchaseReturnRequest(
                context.Today.AddDays(-partialReturn.DaysAgo), partialReturn.Reason, partialReturn.Refund, partialReturn.Refund > 0 ? PaymentMethod.Cash : null,
                partialReturn.DebtReduction, [new ReturnLineInput(line.Id, partialReturn.Quantity)]), cancellationToken), $"return of {plan.Code}");
        }

        if (plan.Outcome == DemoReceiptOutcome.Cancelled)
        {
            DemoSeedContext.Ensure(await context.Get<PurchaseReceiptCancellationService>().CancelAsync(
                receiptId, new CancelReceiptRequest(plan.CancelReason, CancelPayments: true, RowVersion: null), cancellationToken), $"cancelling {plan.Code}");
        }
    }
}
