using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-05: two sales at the port through the service the API uses, by the Zone Manager of each zone. In zone one, a buyer with a profile pays the whole invoice in cash; in zone two,
/// the active demo trader pays 1,000,000 VND in cash and the rest later, through the accepted request that agrees to it (or, when no request was answered, after the Owner's agreement).
/// No lot is closed: the demo lots stay for sale, and the newest lot of each first raft stays removable. Marked done by any <c>SalesInvoice.Created</c> audit row, so a rerun adds nothing.
/// The cash sale ends with the QR code: its lot is the demo's Published lot (TRACE-01 at the end of the sale).
/// </summary>
internal sealed class DemoSalesInvoiceStep : IDemoSeedStep
{
    private const string TraderUsername = "thuonglai1";
    private const string BuyerPhone = "0900000041";

    public string Name => "sales invoices";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        if (await db.AuditEvents.AnyAsync(e => e.Action == SalesInvoiceWriter.CreatedAction, cancellationToken))
        {
            return 0;
        }

        var products = await db.Products.AsNoTracking().Where(p => p.StatusId == StatusIds.Usage.InUse).ToDictionaryAsync(p => p.Name, p => p.Id, cancellationToken);
        var buyer = await db.Customers.AsNoTracking().Where(c => c.PhoneNumber == BuyerPhone && c.MergedIntoCustomerId == null).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        var trader = await db.Customers.AsNoTracking().Where(c => c.Account!.Username == TraderUsername && c.MergedIntoCustomerId == null).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        if (!products.TryGetValue("Hàu sữa", out var milky) || !products.TryGetValue("Hàu béo", out var fat) || buyer is null || trader is null)
        {
            return 0;
        }

        var created = 0;
        if (await OldestLotAsync(context, DemoFarmingZoneStep.ZoneOne, cancellationToken) is { } lotOne)
        {
            var invoiceId = await SellAsync(context, DemoFarmingZoneStep.ZoneOne, "quanlyvung1", new CreateSalesInvoiceRequest(
                buyer, false, lotOne.ZoneId, [new SalesInvoiceLineEntry(milky, lotOne.Id, 120m)], new SalesInvoicePaymentEntry(120m * 60_000m),
                NotSoldWholeLotIds: [lotOne.Id]), cancellationToken);
            await FinishWithQrCodeAsync(context, invoiceId, lotOne.ZoneId, cancellationToken);
            created++;
        }

        if (await OldestLotAsync(context, DemoFarmingZoneStep.ZoneTwo, cancellationToken) is { } lotTwo)
        {
            var request = await db.PurchaseRequests.AsNoTracking()
                .Where(r => r.CustomerId == trader && r.StatusId == StatusIds.PurchaseRequest.Accepted && r.PayLaterAgreed == true && r.AgreedPickupDate >= context.Today)
                .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
            await SellAsync(context, DemoFarmingZoneStep.ZoneTwo, "quanlyvung2", new CreateSalesInvoiceRequest(
                trader, false, lotTwo.ZoneId, [new SalesInvoiceLineEntry(fat, lotTwo.Id, 80m)],
                new SalesInvoicePaymentEntry(1_000_000m, SalesInvoiceRestPayment.PayLater, PayLaterOwnerAgreed: request is null),
                PurchaseRequestId: request, TakesRequestInFull: request is null ? null : false, NotSoldWholeLotIds: [lotTwo.Id]), cancellationToken);
            created++;
        }

        return created;
    }

    /// <summary>
    /// SALE-05 steps 9-11 for the cash sale of zone one: the trader wants the QR code, so TRACE-01 publishes its lot and the invoice gets its QR code and is sent (MSG-SALE-15). The
    /// sending goes to a made-up phone, so with an SMS sender that could reach a real phone the sale is left unfinished (no QR code) and the output says so. The pay-later sale stays
    /// unfinished, for a client to try the end of the sale.
    /// </summary>
    private static async Task FinishWithQrCodeAsync(DemoSeedContext context, Guid invoiceId, Guid zoneId, CancellationToken cancellationToken)
    {
        if (!(context.Get<ITraderMessageSender>() is SmsTraderMessageSender && context.SmsSenderReachesNoPhone))
        {
            context.Notes.Add("Demo sales invoices: the cash sale is not finished and has no QR code, because the SMS sender could reach a real phone.");
            return;
        }

        var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.SalesInvoices, "quanlyvung1", zoneId, cancellationToken);
        using var acting = context.Get<AmbientActor>().Use(actor);
        DemoSeedContext.Ensure(
            await context.Get<SalesInvoiceSendingService>().FinishAsync(invoiceId, new FinishSalesInvoiceRequest(WantsQrCode: true), cancellationToken), "the end of the demo cash sale");
    }

    /// <summary>The first lot of the first raft of the zone, still for sale: the newest one of the raft is left alone, so it can still be removed.</summary>
    private static async Task<(Guid Id, Guid ZoneId)?> OldestLotAsync(DemoSeedContext context, string zoneCode, CancellationToken cancellationToken)
    {
        var raftCode = DemoRaftStep.RaftCode(zoneCode, 1);
        var lot = await context.Db.HarvestLots.AsNoTracking()
            .Where(l => l.Raft.Zone.Code == zoneCode && l.Raft.Code == raftCode && (l.StatusId == StatusIds.HarvestLot.Open || l.StatusId == StatusIds.HarvestLot.Reopened))
            .OrderBy(l => l.LotCode)
            .Select(l => new { l.Id, l.Raft.ZoneId })
            .FirstOrDefaultAsync(cancellationToken);
        return lot is null ? null : (lot.Id, lot.ZoneId);
    }

    private static async Task<Guid> SellAsync(DemoSeedContext context, string zoneCode, string manager, CreateSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        var actor = await DemoCultivationLogStep.ActorAsync(context, PermissionFunctions.SalesInvoices, manager, request.ZoneId!.Value, cancellationToken);
        using var acting = context.Get<AmbientActor>().Use(actor);
        return DemoSeedContext.Ensure(await context.Get<SalesInvoiceService>().CreateAsync(request, cancellationToken), $"sales invoice of zone {zoneCode}").Saved!.Invoice.Id;
    }
}
