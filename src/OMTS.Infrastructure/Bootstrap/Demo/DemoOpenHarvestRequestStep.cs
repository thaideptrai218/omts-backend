using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// One more Accepted purchase request of the demo trader, whose harvest is still open, for the Owner to record a NEW harvest lot against through the API (a lot must name an
/// Accepted request whose harvest is not marked done, decision of 11/11/2026). <see cref="DemoPurchaseRequestStep"/> only runs on a fresh trader and its Accepted request is used by the
/// demo lots, so this step runs on its own and is matched by a fixed operation id, never by the trader having requests: a rerun adds nothing, also on a database seeded before this step.
/// The request is written Accepted directly, as that step writes its own, because an accepted request through the API needs a work plan and staff the demo has none of; the rules a lot
/// is recorded under (<c>PurchaseRequestRules.RefusesHarvestLotAsync</c>) look at the status, the harvest mark and the invoices only, and are not changed. No SMS or push is queued, so no sender is
/// reached. It moves no stock: a request reserves nothing (BR-SALE-01). It is made only when a raft of an In Use zone has an Open cycle and a usable lane (In Use, not damaged, not out of service)
/// to harvest, and it asks for no pay later, so the demo sales step never picks it. It runs after the lots and the sales of the demo, so neither takes it.
/// </summary>
internal sealed class DemoOpenHarvestRequestStep : IDemoSeedStep
{
    private const string TraderUsername = "thuonglai1";
    private const string ProductName = "Hàu béo";

    /// <summary>What finds the request again: the same on every run. Unique per customer, as the API's retry key is.</summary>
    internal static readonly Guid OperationId = new("0d132a00-0000-4000-8000-000000000132");

    public string Name => "open harvest request";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Account != null && c.Account.Username == TraderUsername, cancellationToken);
        if (customer is null || await db.PurchaseRequests.AnyAsync(r => r.CustomerId == customer.Id && r.ClientOperationId == OperationId, cancellationToken))
        {
            return 0;
        }

        var product = await db.Products.FirstOrDefaultAsync(p => p.Name == ProductName && p.StatusId == StatusIds.Usage.InUse, cancellationToken);
        var hasRaftToHarvest = await db.CultivationCycles.AsNoTracking()
            .AnyAsync(c => c.StatusId == StatusIds.CultivationCycle.Open && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse
                && c.Raft.Lanes.Any(l => l.StatusId == StatusIds.Lane.InUse && !l.IsDamaged && l.OutOfServiceOn == null), cancellationToken);
        if (product is null || !hasRaftToHarvest)
        {
            context.Notes.Add("Demo open harvest request: no product or no raft with an Open cycle and a usable lane, so no request was made.");
            return 0;
        }

        var request = new PurchaseRequest
        {
            CustomerId = customer.Id,
            Customer = customer,
            ProductId = product.Id,
            Product = product,
            ProductName = product.Name,
            RequestedCages = 5,
            PickupDate = context.Today.AddDays(4),
            Note = "Lấy thêm một chuyến cuối tuần",
            ClientOperationId = OperationId,
            SentAt = context.Now,
            StatusId = StatusIds.PurchaseRequest.Accepted,
            AcceptedCages = 5,
            AgreedPickupDate = context.Today.AddDays(4),
            PayLaterAgreed = false,
            ResponseNote = "Đồng ý lấy 5 lồng, thanh toán khi nhận hàng",
            RespondedAt = context.Now,
            RespondedByAccountId = context.OwnerId,
        };
        request.Code = $"YC-{context.Today:yyyyMMdd}-{request.Id.ToString("N")[..12].ToUpperInvariant()}";
        db.PurchaseRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        context.Notes.Add($"Demo open harvest request: {request.Code} is Accepted and open for harvest: a new lot can be recorded for it on a raft with a seeded lane.");
        return 1;
    }
}
