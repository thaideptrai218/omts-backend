using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Notifications;

public class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeNotificationRepository _notifications = new();
    private readonly FakePushOutbox _outbox = new();

    [Fact]
    public async Task Non_mandatory_type_follows_each_accounts_channel_preferences()
    {
        var silent = Guid.NewGuid();
        var pushOnly = Guid.NewGuid();
        var defaults = Guid.NewGuid();
        _notifications.Preferences[silent] = [Off(NotificationCategories.DeadlineReminders, NotificationChannel.InApp), Off(NotificationCategories.DeadlineReminders, NotificationChannel.Push)];
        _notifications.Preferences[pushOnly] = [Off(NotificationCategories.DeadlineReminders, NotificationChannel.InApp)];
        _notifications.Preferences[defaults] = [];

        await NotifyAsync([silent, pushOnly, defaults], NotificationTypes.ZoneLicenseExpiry, isMandatory: false);

        Assert.Equal([defaults], _notifications.Added.Select(n => n.RecipientAccountId));
        Assert.Equal([pushOnly, defaults], _outbox.Messages.Select(m => m.RecipientAccountId));
    }

    [Fact]
    public async Task Mandatory_type_is_delivered_in_app_and_by_push_even_when_turned_off()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [Off(NotificationCategories.WaterAlerts, NotificationChannel.InApp), Off(NotificationCategories.WaterAlerts, NotificationChannel.Push)];

        await NotifyAsync([account], NotificationTypes.WaterAlert, isMandatory: false);

        var notification = Assert.Single(_notifications.Added);
        Assert.True(notification.IsMandatory);
        Assert.Equal(Now, notification.CreatedAt);
        Assert.Single(_outbox.Messages);
    }

    [Fact]
    public async Task Unknown_accounts_and_duplicates_are_skipped()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [];

        await NotifyAsync([account, account, Guid.NewGuid()], NotificationTypes.WorkAssignment, isMandatory: false);

        Assert.Single(_notifications.Added);
        Assert.Single(_outbox.Messages);
    }

    private Task NotifyAsync(IReadOnlyCollection<Guid> recipients, string type, bool isMandatory) =>
        new NotificationDispatcher(_notifications, _outbox, new FixedTimeProvider(Now))
            .NotifyAsync(recipients, type, "Title", "Body", "Record", "R-1", isMandatory, CancellationToken.None);

    private static NotificationPreference Off(string category, NotificationChannel channel) =>
        new() { Category = category, Channel = channel, Enabled = false };

    private sealed class FakeNotificationRepository : INotificationRepository
    {
        public Dictionary<Guid, IReadOnlyList<NotificationPreference>> Preferences { get; } = [];
        public List<Notification> Added { get; } = [];

        public void Add(Notification notification) => Added.Add(notification);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
            IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>>(
                Preferences.Where(p => accountIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    private sealed class FakePushOutbox : IPushOutbox
    {
        public List<PushMessage> Messages { get; } = [];

        public void Enqueue(PushMessage message) => Messages.Add(message);
    }
}
