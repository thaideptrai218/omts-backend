using System.Text.Json;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>BR-ACCOUNT-11/12: every notification is kept in the list of each active recipient; a mandatory type also queues its SMS.</summary>
public class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeNotificationRepository _notifications = new();
    private readonly MandatorySmsQueueFakes _sms = new();

    [Fact]
    public async Task A_normal_type_is_kept_for_every_active_recipient_and_is_not_mandatory()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _notifications.Active.UnionWith([first, second]);

        await NotifyAsync([first, second], NotificationTypes.WaterMeasurementOverdue);

        Assert.Equal([first, second], _notifications.Added.Select(n => n.RecipientAccountId!.Value));
        Assert.All(_notifications.Added, n => Assert.False(n.IsMandatory));
    }

    [Fact]
    public async Task A_mandatory_type_is_kept_and_marked_mandatory()
    {
        var account = Guid.NewGuid();
        _notifications.Active.Add(account);

        await NotifyAsync([account], NotificationTypes.WaterAlert);
        await NotifyAsync([account], NotificationTypes.ZoneLicenseExpiry);

        Assert.Equal(2, _notifications.Added.Count);
        Assert.All(_notifications.Added, n => Assert.True(n.IsMandatory));
        Assert.Equal(Now, _notifications.Added[0].CreatedAt);
    }

    [Fact]
    public async Task A_type_kept_in_the_list_only_is_kept_and_not_mandatory()
    {
        var account = Guid.NewGuid();
        _notifications.Active.Add(account);

        await NotifyAsync([account], NotificationTypes.StaffAccountLockReminder);

        var kept = Assert.Single(_notifications.Added);
        Assert.Equal(NotificationTypes.StaffAccountLockReminder, kept.Type);
        Assert.False(kept.IsMandatory);
    }

    [Fact]
    public async Task The_row_keeps_the_message_code_its_parameters_and_the_related_record()
    {
        var account = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        _notifications.Active.Add(account);

        await new NotificationDispatcher(_notifications, _sms.Queue, new FixedTimeProvider(Now)).NotifyAsync(
            [account],
            new NotificationContent(
                NotificationTypes.WaterMeasurementOverdue,
                "Water measurement overdue",
                UserMessages.WaterMeasurementOverdue("Vùng Cái Rồng", new DateOnly(2026, 10, 1)),
                new RelatedRecord(RelatedRecordTypes.FarmingZone, zoneId, "KV01")),
            CancellationToken.None);

        var row = Assert.Single(_notifications.Added);
        Assert.Equal(NotificationChannel.InApp, row.Channel);
        Assert.Equal("MSG-WATER-06", row.MessageCode);
        Assert.Equal("Zone Vùng Cái Rồng has had no water measurement since 01/10/2026. Please measure the water and record the result in OMTS.", row.Body);
        // Atomic values: an ISO date, readable Vietnamese.
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(row.MessageParams!)!;
        Assert.Equal(new Dictionary<string, string> { ["zone_name"] = "Vùng Cái Rồng", ["last_date"] = "2026-10-01" }, parameters);
        Assert.Contains("Vùng Cái Rồng", row.MessageParams);
        Assert.Equal((RelatedRecordTypes.FarmingZone, zoneId, "KV01"), (row.RelatedRecordType, row.RelatedRecordId, row.RelatedRecordCode));
    }

    [Fact]
    public async Task Unknown_or_locked_accounts_and_duplicates_are_skipped()
    {
        var account = Guid.NewGuid();
        _notifications.Active.Add(account);

        await NotifyAsync([account, account, Guid.NewGuid()], NotificationTypes.WaterMeasurementOverdue);

        Assert.Single(_notifications.Added);
    }

    [Fact]
    public async Task An_unregistered_type_is_refused()
    {
        var account = Guid.NewGuid();
        _notifications.Active.Add(account);

        await Assert.ThrowsAsync<ArgumentException>(() => NotifyAsync([account], "NOT_A_NOTIFICATION_TYPE"));
        Assert.Empty(_notifications.Added);
    }

    [Fact]
    public void Every_registered_type_has_a_unique_code_and_a_role()
    {
        Assert.Equal(NotificationTypes.All.Count, NotificationTypes.All.Select(t => t.Code).Distinct().Count());
        Assert.All(NotificationTypes.All, t => Assert.NotEmpty(t.Roles));
    }

    private Task NotifyAsync(IReadOnlyCollection<Guid> recipients, string type) =>
        new NotificationDispatcher(_notifications, _sms.Queue, new FixedTimeProvider(Now))
            .NotifyAsync(recipients, new NotificationContent(type, "Title", new UserMessage("MSG-WATER-06", "Body")), CancellationToken.None);

    /// <summary>Accounts in <see cref="Active"/> are the active ones.</summary>
    private sealed class FakeNotificationRepository : INotificationRepository
    {
        public HashSet<Guid> Active { get; } = [];
        public List<Notification> Added { get; } = [];

        public void Add(Notification notification) => Added.Add(notification);

        public Task<IReadOnlySet<Guid>> GetActiveAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(Active.Where(accountIds.Contains).ToHashSet());

        public Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(
            Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountRecordsNotifiedSinceAsync(string type, DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AnyOfTypeSinceAsync(string type, DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
