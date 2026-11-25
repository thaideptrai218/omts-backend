using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>A Mini App notification always names the record it opens: the id it is given, never an empty one.</summary>
public class MiniAppNotificationExtensionsTests
{
    [Fact]
    public async Task The_notification_opens_the_record_with_the_id_it_was_given()
    {
        var dispatcher = new CapturingDispatcher();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();

        await dispatcher.NotifyAsync([owner], NotificationTypes.TraderAccessRequested, "Trader account request", "A trader asks for an account.", "Account", "zalo-user", isMandatory: false, CancellationToken.None, account);

        var content = Assert.Single(dispatcher.Sent);
        Assert.Equal(new RelatedRecord("Account", account, "zalo-user"), content.Record);
        Assert.NotEqual(Guid.Empty, content.Record!.Id);
    }

    private sealed class CapturingDispatcher : INotificationDispatcher
    {
        public List<NotificationContent> Sent { get; } = [];

        public Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken)
        {
            Sent.Add(content);
            return Task.CompletedTask;
        }
    }
}
