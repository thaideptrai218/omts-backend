using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-01 to SALE-03: the active demo trader's requests, one in each state, through the services the API uses. The trader's
/// first request (its PurchaseRequest.Sent audit row) marks the step as done, so a rerun neither repeats them nor undoes what
/// happened since. A response sends the demo number an SMS, so the two answers are made only when the sender is the Fake or
/// None, which reach no phone. With any other sender the requests stay Awaiting response and the output says so.
/// </summary>
internal sealed class DemoPurchaseRequestStep : IDemoSeedStep
{
    private const string TraderUsername = "thuonglai1";

    public string Name => "purchase requests";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var trader = await context.Db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Username == TraderUsername && a.SubjectType == SubjectType.Trader, cancellationToken);
        if (trader is null
            || await context.Db.AuditEvents.AnyAsync(e => e.Action == "PurchaseRequest.Sent" && e.ActorAccountId == trader.Id, cancellationToken))
        {
            return 0;
        }

        // Matched by name; a product renamed or discontinued since is simply not requested.
        var products = await context.Db.Products.AsNoTracking()
            .Where(p => p.StatusId == Domain.Constants.StatusIds.Usage.InUse)
            .ToDictionaryAsync(p => p.Name, p => p.Id, cancellationToken);
        if (!products.TryGetValue("Hàu sữa", out var milky) || !products.TryGetValue("Hàu béo", out var fat) || !products.TryGetValue("Hàu sống loại nhỏ", out var small))
        {
            return 0;
        }

        var answer = ReachesNoPhone(context);
        if (!answer)
        {
            context.Notes.Add("Demo purchase requests: the SMS sender is neither Fake nor None, so no request was answered and no SMS was handed over.");
        }

        var traders = context.Get<TraderPurchaseRequestService>();
        var responses = context.Get<PurchaseRequestResponseService>();
        async Task<TraderPurchaseRequestView> PlaceAsync(Guid productId, decimal kg, int inDays, string note) => DemoSeedContext.Ensure(
            await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(productId, kg, context.Today.AddDays(inDays), note), cancellationToken), "purchase request").Request;

        await PlaceAsync(milky, 500, 2, "Xin lấy hàng sáng sớm ở cảng Cái Rồng");

        var accepted = await PlaceAsync(fat, 300, 1, "Cần cho quán dịp cuối tuần");
        if (answer)
        {
            DemoSeedContext.Ensure(await responses.AcceptAsync(
                accepted.Id, new AcceptPurchaseRequest(250, context.Today.AddDays(2), true, "Bè còn đủ 250 kg, trả sau như đã thỏa thuận", accepted.RowVersion), cancellationToken), "accepting a purchase request");
        }

        var declined = await PlaceAsync(small, 1000, 3, "Lấy nhiều nếu có giá tốt");
        if (answer)
        {
            DemoSeedContext.Ensure(await responses.DeclineAsync(
                declined.Id, new DeclinePurchaseRequest("Tuần này chưa đủ hàu đạt cỡ", declined.RowVersion), cancellationToken), "declining a purchase request");
        }

        var cancelled = await PlaceAsync(milky, 200, 5, "Đơn thử");
        DemoSeedContext.Ensure(await traders.CancelAsync(
            trader.Id, cancelled.Id, new CancelPurchaseRequest("Khách đổi lịch", cancelled.RowVersion), cancellationToken), "cancelling a purchase request");

        return 4;
    }

    // By what would really receive the text, not by the name in Sms:Provider: anything not known to be harmless is treated as real.
    private static bool ReachesNoPhone(DemoSeedContext context) =>
        context.Get<ITraderMessageSender>() is SmsTraderMessageSender
        && context.Get<ISmsSender>() is FakeSmsSender or UnconfiguredSmsSender;
}
