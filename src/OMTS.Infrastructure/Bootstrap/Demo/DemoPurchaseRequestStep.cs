using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-01 to SALE-03: the active demo trader's requests in cages, one in each state. A request accepted through the API needs a
/// work plan and enough staff, which the demo has none of, so the rows are written here with the notices the API sends. The
/// trader's first request marks the step as done, so a rerun neither repeats them nor undoes what happened since. A response
/// sends the demo number an SMS, so the two answers are made only when the sender is the Fake or None, which reach no phone.
/// With any other sender the requests stay Awaiting response and the output says so.
/// </summary>
internal sealed class DemoPurchaseRequestStep : IDemoSeedStep
{
    private const string TraderUsername = "thuonglai1";

    public string Name => "purchase requests";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var customer = await context.Db.Customers.FirstOrDefaultAsync(c => c.Account != null && c.Account.Username == TraderUsername, cancellationToken);
        if (customer is null || await context.Db.PurchaseRequests.AnyAsync(r => r.CustomerId == customer.Id, cancellationToken))
        {
            return 0;
        }

        // Matched by name; a product renamed or discontinued since is simply not requested.
        var products = await context.Db.Products.Where(p => p.StatusId == StatusIds.Usage.InUse).ToDictionaryAsync(p => p.Name, cancellationToken);
        if (!products.TryGetValue("Hàu sữa", out var milky) || !products.TryGetValue("Hàu béo", out var fat) || !products.TryGetValue("Hàu sống loại nhỏ", out var small))
        {
            return 0;
        }

        var answer = ReachesNoPhone(context);
        if (!answer)
        {
            context.Notes.Add("Demo purchase requests: the SMS sender is neither Fake nor None, so no request was answered and no SMS was handed over.");
        }

        var notices = context.Get<PurchaseRequestNotifications>();
        var sendings = new List<Notification>();

        PurchaseRequest Place(Product product, int cages, int inDays, string note)
        {
            var request = new PurchaseRequest
            {
                CustomerId = customer.Id,
                Customer = customer,
                ProductId = product.Id,
                Product = product,
                ProductName = product.Name,
                RequestedCages = cages,
                PickupDate = context.Today.AddDays(inDays),
                Note = note,
                ClientOperationId = Guid.NewGuid(),
                SentAt = context.Now,
            };
            request.Code = $"YC-{context.Today:yyyyMMdd}-{request.Id.ToString("N")[..12].ToUpperInvariant()}";
            context.Db.PurchaseRequests.Add(request);
            return request;
        }

        async Task AnswerAsync(PurchaseRequest request, int statusId, string note)
        {
            request.StatusId = statusId;
            request.ResponseNote = note;
            request.RespondedAt = context.Now;
            request.RespondedByAccountId = context.OwnerId;
            if (await notices.ResponseAsync(request, cancellationToken) is { } sending)
            {
                sendings.Add(sending);
            }
        }

        var awaiting = Place(milky, 10, 2, "Xin lấy hàng sáng sớm ở cảng Cái Rồng");
        await notices.NewRequestAsync(awaiting, cancellationToken);
        // Saved on its own, as the API saves each request it is sent: a seed that fails on a later request keeps this one, and a rerun
        // sees the trader's first request and adds nothing.
        await context.Db.SaveChangesAsync(cancellationToken);

        var accepted = Place(fat, 6, 1, "Cần cho quán dịp cuối tuần");
        await notices.NewRequestAsync(accepted, cancellationToken);
        if (answer)
        {
            accepted.AcceptedCages = 5;
            accepted.AgreedPickupDate = context.Today.AddDays(2);
            accepted.PayLaterAgreed = true;
            await AnswerAsync(accepted, StatusIds.PurchaseRequest.Accepted, "Bè còn đủ 5 lồng, trả sau như đã thỏa thuận");
        }

        var declined = Place(small, 20, 3, "Lấy nhiều nếu có giá tốt");
        await notices.NewRequestAsync(declined, cancellationToken);
        if (answer)
        {
            await AnswerAsync(declined, StatusIds.PurchaseRequest.Declined, "Tuần này chưa đủ hàu đạt cỡ");
        }

        var cancelled = Place(milky, 4, 5, "Đơn thử");
        await notices.NewRequestAsync(cancelled, cancellationToken);
        cancelled.StatusId = StatusIds.PurchaseRequest.Cancelled;
        cancelled.CancelReason = "Khách đổi lịch";
        cancelled.CancelledAt = context.Now;
        await notices.CancelledAsync(cancelled, cancellationToken);

        await context.Db.SaveChangesAsync(cancellationToken);
        var messages = context.Get<TraderMessageService>();
        foreach (var sending in sendings)
        {
            await messages.DeliverAsync(sending);
        }

        return 4;
    }

    private static bool ReachesNoPhone(DemoSeedContext context) =>
        context.Get<ITraderMessageSender>() is SmsTraderMessageSender && context.SmsSenderReachesNoPhone;
}
