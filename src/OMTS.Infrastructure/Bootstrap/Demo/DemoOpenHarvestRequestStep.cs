using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// Accepted purchase requests whose harvest is still open, for the Owner to record NEW harvest lots against through the API (a lot must name an Accepted request whose harvest is not marked done, decision of
/// 11/11/2026). <see cref="DemoPurchaseRequestStep"/> only runs on a fresh trader and its Accepted request is used by the demo lots, so this step runs on its own and every request is matched by a fixed
/// operation id, never by the customer having requests: a rerun adds nothing, also on a database seeded before this step or before a request was added to it. There are four, all agreed for the next days: 5 cages
/// of the demo trader (<c>thuonglai1</c>), and, for the live demo of the whole farming flow, three orders of about 800, 1,500 and 2,000 kg (a cage holds about 40 kg: 20, 38 and 50 cages, as the demo weighing
/// takes it) of the offline customers "Vựa hải sản Bà Lan", "Nhà hàng Biển Xanh" and "Anh Tùng thương lái Cẩm Phả" of <see cref="DemoCustomerStep"/>. The requests are written Accepted directly, as that step writes
/// its own, because an accepted request through the API needs a work plan and staff the demo has none of; the rules a lot is recorded under (<c>PurchaseRequestRules.RefusesHarvestLotAsync</c>) look at the status,
/// the harvest mark and the invoices only, and are not changed. No SMS or push is queued, so no sender is reached. They move no stock: a request reserves nothing (BR-SALE-01). They are made only when a raft of
/// an In Use zone has an Open cycle and a usable lane (In Use, not damaged, not out of service) to harvest, and none asks for pay later, so the demo sales step never picks them. They run after the lots and the
/// sales of the demo, so neither takes them.
/// </summary>
internal sealed class DemoOpenHarvestRequestStep : IDemoSeedStep
{
    private const string ProductName = "Hàu béo";

    /// <param name="OperationId">What finds the request again: the same on every run. Unique per customer, as the API's retry key is.</param>
    /// <param name="TraderUsername">The trader account of the customer; null to find the customer by <paramref name="Phone"/>.</param>
    private sealed record Plan(Guid OperationId, string? TraderUsername, string? Phone, int Cages, int PickupInDays, string Note, string ResponseNote);

    private static readonly Plan[] Plans =
    [
        new(new("0d132a00-0000-4000-8000-000000000132"), "thuonglai1", null, 5, 4, "Lấy thêm một chuyến cuối tuần", "Đồng ý lấy 5 lồng, thanh toán khi nhận hàng"),
        new(new("0d132a00-0000-4000-8000-000000000133"), null, "0900000041", 20, 3, "Cần khoảng 800 kg hàu béo cho vựa (khoảng 20 lồng)", "Đồng ý 20 lồng (khoảng 800 kg), lấy tại cảng Cái Rồng"),
        new(new("0d132a00-0000-4000-8000-000000000134"), null, "0900000042", 38, 5, "Cần khoảng 1.500 kg hàu béo cho nhà hàng (khoảng 38 lồng)", "Đồng ý 38 lồng (khoảng 1.500 kg), giao theo lịch thu hoạch"),
        new(new("0d132a00-0000-4000-8000-000000000135"), null, "0900000043", 50, 6, "Cần khoảng 2.000 kg hàu béo chở đi Cẩm Phả (khoảng 50 lồng)", "Đồng ý 50 lồng (khoảng 2.000 kg), thu hoạch trong hai ngày"),
    ];

    public string Name => "open harvest request";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var product = await db.Products.FirstOrDefaultAsync(p => p.Name == ProductName && p.StatusId == StatusIds.Usage.InUse, cancellationToken);
        var hasRaftToHarvest = await db.CultivationCycles.AsNoTracking()
            .AnyAsync(c => c.StatusId == StatusIds.CultivationCycle.Open && c.Raft.StatusId == StatusIds.Usage.InUse && c.Raft.Zone.StatusId == StatusIds.Usage.InUse
                && c.Raft.Lanes.Any(l => l.StatusId == StatusIds.Lane.InUse && !l.IsDamaged && l.OutOfServiceOn == null), cancellationToken);
        var created = 0;
        foreach (var plan in Plans)
        {
            var customer = plan.TraderUsername is { } username
                ? await db.Customers.FirstOrDefaultAsync(c => c.Account != null && c.Account.Username == username, cancellationToken)
                : await db.Customers.FirstOrDefaultAsync(c => c.PhoneNumber == plan.Phone, cancellationToken);
            if (customer is null || await db.PurchaseRequests.AnyAsync(r => r.CustomerId == customer.Id && r.ClientOperationId == plan.OperationId, cancellationToken))
            {
                continue;
            }

            if (product is null || !hasRaftToHarvest)
            {
                context.Notes.Add("Demo open harvest request: no product or no raft with an Open cycle and a usable lane, so no request was made.");
                break;
            }

            var pickup = context.Today.AddDays(plan.PickupInDays);
            var request = new PurchaseRequest
            {
                CustomerId = customer.Id,
                Customer = customer,
                ProductId = product.Id,
                Product = product,
                ProductName = product.Name,
                RequestedCages = plan.Cages,
                PickupDate = pickup,
                Note = plan.Note,
                ClientOperationId = plan.OperationId,
                SentAt = context.Now,
                StatusId = StatusIds.PurchaseRequest.Accepted,
                AcceptedCages = plan.Cages,
                AgreedPickupDate = pickup,
                PayLaterAgreed = false,
                ResponseNote = plan.ResponseNote,
                RespondedAt = context.Now,
                RespondedByAccountId = context.OwnerId,
            };
            request.Code = $"YC-{context.Today:yyyyMMdd}-{request.Id.ToString("N")[..12].ToUpperInvariant()}";
            db.PurchaseRequests.Add(request);
            await db.SaveChangesAsync(cancellationToken);
            context.Notes.Add($"Demo open harvest request: {request.Code} ({customer.Name}, {plan.Cages} cages) is Accepted and open for harvest: a new lot can be recorded for it on a raft with a seeded lane.");
            created++;
        }

        return created;
    }
}
