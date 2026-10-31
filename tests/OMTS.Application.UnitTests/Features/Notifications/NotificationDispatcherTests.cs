using System.Text.Json;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>BR-ACCOUNT-11/12: every notification is kept; push follows the type's policy and the account's preference.</summary>
public class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 0, 0, TimeSpan.Zero);

    private readonly FakeNotificationRepository _notifications = new();
    private readonly FakePushOutbox _outbox = new();
    private readonly FakePushDeviceRepository _devices = new();

    [Fact]
    public async Task A_normal_type_is_kept_for_everyone_and_pushed_unless_push_is_turned_off()
    {
        var pushOff = Guid.NewGuid();
        var pushOn = Guid.NewGuid();
        var defaults = Guid.NewGuid();
        _notifications.Preferences[pushOff] = [Push(NotificationTypes.WaterMeasurementOverdue, false)];
        _notifications.Preferences[pushOn] = [Push(NotificationTypes.WaterMeasurementOverdue, true)];
        _notifications.Preferences[defaults] = [Push(NotificationTypes.WaterAlert, false)];

        await NotifyAsync([pushOff, pushOn, defaults], NotificationTypes.WaterMeasurementOverdue);

        Assert.Equal([pushOff, pushOn, defaults], _notifications.Added.Select(n => n.RecipientAccountId));
        Assert.Equal([pushOn, defaults], _outbox.Messages.Select(m => m.RecipientAccountId));
        Assert.All(_notifications.Added, n => Assert.False(n.IsMandatory));
    }

    [Fact]
    public async Task A_mandatory_type_is_kept_and_pushed_even_when_its_push_is_turned_off()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [Push(NotificationTypes.WaterAlert, false), Push(NotificationTypes.ZoneLicenseExpiry, false)];

        await NotifyAsync([account], NotificationTypes.WaterAlert);
        await NotifyAsync([account], NotificationTypes.ZoneLicenseExpiry);

        Assert.Equal(2, _notifications.Added.Count);
        Assert.All(_notifications.Added, n => Assert.True(n.IsMandatory));
        Assert.Equal(Now, _notifications.Added[0].CreatedAt);
        Assert.Equal(2, _outbox.Messages.Count);
    }

    [Fact]
    public async Task A_never_push_type_is_kept_and_never_pushed()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [Push(NotificationTypes.StaffAccountLockReminder, true)];
        _devices.Deliverable.Add(Device(account, "phone"));

        await NotifyAsync([account], NotificationTypes.StaffAccountLockReminder);

        var kept = Assert.Single(_notifications.Added);
        Assert.Equal(NotificationTypes.StaffAccountLockReminder, kept.Type);
        Assert.False(kept.IsMandatory);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task The_row_keeps_the_message_code_its_parameters_and_the_related_record()
    {
        var account = Guid.NewGuid();
        var zoneId = Guid.NewGuid();
        _notifications.Preferences[account] = [];

        await new NotificationDispatcher(_notifications, _devices, _outbox, new FixedTimeProvider(Now)).NotifyAsync(
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
        // The push carries the code and the parameters, so the device builds the text in its language.
        var push = Assert.Single(_outbox.Messages);
        Assert.Equal((row.Id, "MSG-WATER-06", zoneId, row.MessageParams), (push.NotificationId, push.MessageCode, push.RelatedRecordId, push.MessageParams));
    }

    [Fact]
    public async Task Unknown_or_locked_accounts_and_duplicates_are_skipped()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [];

        await NotifyAsync([account, account, Guid.NewGuid()], NotificationTypes.WaterMeasurementOverdue);

        Assert.Single(_notifications.Added);
        Assert.Single(_outbox.Messages);
    }

    [Fact]
    public async Task An_unregistered_type_is_refused()
    {
        var account = Guid.NewGuid();
        _notifications.Preferences[account] = [];

        await Assert.ThrowsAsync<ArgumentException>(() => NotifyAsync([account], "WORK_ASSIGNMENT"));
        Assert.Empty(_notifications.Added);
    }

    [Fact]
    public async Task Push_carries_only_the_recipients_deliverable_device_tokens()
    {
        var withDevices = Guid.NewGuid();
        var withoutDevice = Guid.NewGuid();
        _notifications.Preferences[withDevices] = [];
        _notifications.Preferences[withoutDevice] = [];
        _devices.Deliverable.AddRange([Device(withDevices, "phone"), Device(withDevices, "tablet"), Device(Guid.NewGuid(), "someone-else")]);

        await NotifyAsync([withDevices, withoutDevice], NotificationTypes.WaterAlert);

        Assert.Equal(["phone", "tablet"], _outbox.Messages.Single(m => m.RecipientAccountId == withDevices).DeviceTokens);
        Assert.Empty(_outbox.Messages.Single(m => m.RecipientAccountId == withoutDevice).DeviceTokens);
        Assert.Equal(Now, _devices.AskedAt);
    }

    [Fact]
    public void Every_registered_type_is_offered_to_a_role_and_never_push_types_are_not_offered()
    {
        Assert.Equal(NotificationTypes.All.Count, NotificationTypes.All.Select(t => t.Code).Distinct().Count());
        Assert.All(NotificationTypes.All, t => Assert.NotEmpty(t.Roles));
        var everyRole = Enum.GetValues<NotificationRole>().ToHashSet();
        Assert.DoesNotContain(NotificationTypes.OfferedTo(everyRole), t => t.PushPolicy == NotificationPushPolicy.NeverPush);
    }

    private Task NotifyAsync(IReadOnlyCollection<Guid> recipients, string type) =>
        new NotificationDispatcher(_notifications, _devices, _outbox, new FixedTimeProvider(Now))
            .NotifyAsync(recipients, new NotificationContent(type, "Title", new UserMessage("MSG-WATER-06", "Body")), CancellationToken.None);

    private static PushDevice Device(Guid accountId, string token) => new() { AccountId = accountId, Token = token, PermissionGranted = true };

    private static NotificationPreference Push(string type, bool push) => new() { Type = type, Push = push };

    /// <summary>Accounts in <see cref="Preferences"/> are the active ones.</summary>
    private sealed class FakeNotificationRepository : INotificationRepository
    {
        public Dictionary<Guid, IReadOnlyList<NotificationPreference>> Preferences { get; } = [];
        public List<Notification> Added { get; } = [];

        public void Add(Notification notification) => Added.Add(notification);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(
            IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>>(
                Preferences.Where(p => accountIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));

        public Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(
            Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakePushOutbox : IPushOutbox
    {
        public List<PushMessage> Messages { get; } = [];

        public void Enqueue(PushMessage message) => Messages.Add(message);
    }

    /// <summary>The rows it holds are the deliverable ones; the session rule is tested against the database.</summary>
    private sealed class FakePushDeviceRepository : IPushDeviceRepository
    {
        public List<PushDevice> Deliverable { get; } = [];
        public DateTimeOffset? AskedAt { get; private set; }

        public Task<IReadOnlyList<PushDevice>> ListDeliverableAsync(
            IReadOnlyCollection<Guid> accountIds, DateTimeOffset now, CancellationToken cancellationToken)
        {
            AskedAt = now;
            return Task.FromResult<IReadOnlyList<PushDevice>>(Deliverable.Where(d => accountIds.Contains(d.AccountId)).ToList());
        }

        public Task<PushDevice?> GetByTokenAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PushDevice>> ListForSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(PushDevice device) => throw new NotSupportedException();

        public void Remove(PushDevice device) => throw new NotSupportedException();
    }
}
