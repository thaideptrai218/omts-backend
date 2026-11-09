using Microsoft.Extensions.Logging.Abstractions;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>BR-SALE-09, GFR-06: Sending, then Sent or Not sent; whatever the service or the second save does, nothing is thrown.</summary>
public class TraderMessageServiceTests
{
    private static readonly NotificationContent Content = new(
        NotificationTypes.PurchaseRequestResponse,
        "Response",
        UserMessages.PurchaseRequestDeclined("HTX", 10m, "Hàu sữa", "Chưa đủ"),
        new RelatedRecord(RelatedRecordTypes.PurchaseRequest, Guid.NewGuid()));

    private static (TraderMessageService Service, Stubs Stubs) Build(Func<CancellationToken, Task<DeliveryResult>> send)
    {
        var stubs = new Stubs(send);
        return (new TraderMessageService(stubs, stubs, stubs, TimeProvider.System, NullLogger<TraderMessageService>.Instance), stubs);
    }

    [Fact]
    public void Queue_adds_a_sending_row_outside_the_list()
    {
        var (service, stubs) = Build(_ => Task.FromResult(DeliveryResult.Sent("id")));

        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        Assert.Same(row, stubs.Added);
        Assert.Equal((NotificationChannel.Sms, "0900000099", StatusIds.MessageDelivery.Sending, "MSG-SALE-03"), (row.Channel, row.PhoneNumber, row.DeliveryStatusId, row.MessageCode));
        Assert.NotNull(row.DeliveryStatusAt);
        Assert.Equal(0, stubs.Saves);
    }

    [Fact]
    public async Task An_accepted_message_is_sent_with_the_id_of_the_service()
    {
        var (service, stubs) = Build(_ => Task.FromResult(DeliveryResult.Sent("provider-1")));
        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        await service.DeliverAsync(row);

        Assert.Equal((StatusIds.MessageDelivery.Sent, "provider-1", (string?)null, 1), (row.DeliveryStatusId, row.ProviderMessageId, row.ProviderErrorCode, stubs.Saves));
    }

    [Fact]
    public async Task An_accepted_message_with_a_long_id_stays_sent_and_the_id_fits_the_column()
    {
        var (service, stubs) = Build(_ => Task.FromResult(DeliveryResult.Sent(new string('m', 300))));
        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        await service.DeliverAsync(row);

        Assert.Equal((StatusIds.MessageDelivery.Sent, new string('m', 100), 1), (row.DeliveryStatusId, row.ProviderMessageId, stubs.Saves));
    }

    [Fact]
    public async Task A_refused_message_is_not_sent_and_a_long_error_code_fits_the_column()
    {
        var (service, _) = Build(_ => Task.FromResult(DeliveryResult.Failed(new string('E', 300))));
        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        await service.DeliverAsync(row);

        Assert.Equal(StatusIds.MessageDelivery.NotSent, row.DeliveryStatusId);
        Assert.Equal(100, row.ProviderErrorCode!.Length);
    }

    [Fact]
    public async Task An_exception_of_the_service_is_not_sent()
    {
        var (service, _) = Build(_ => throw new InvalidOperationException("down"));
        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        await service.DeliverAsync(row);

        Assert.Equal((StatusIds.MessageDelivery.NotSent, "SEND_FAILED"), (row.DeliveryStatusId, row.ProviderErrorCode));
    }

    // QA L1: MSG-TRACE-05 lists every invoice of the trader; a trader on many invoices must not make the caller's save fail on the 2,000-character columns (as NotificationDispatcher).
    [Fact]
    public void Queue_cuts_a_long_text_to_the_column_and_leaves_out_parameters_that_do_not_fit()
    {
        var (service, _) = Build(_ => Task.FromResult(DeliveryResult.Sent("id")));
        var invoiceCodes = Enumerable.Range(1, 400).Select(i => $"HD{i:D6}").ToList();
        var message = UserMessages.ProvenanceWithdrawnNotice("DEMO HTX", "LOT-DEMO-01", invoiceCodes, null);
        Assert.True(message.Text.Length > 2000);

        var row = service.Queue(Guid.NewGuid(), "999900000001", Content with { Type = NotificationTypes.ProvenanceWithdrawn, Message = message });

        Assert.Equal(message.Text[..2000], row.Body);
        Assert.Null(row.MessageParams);
        Assert.Equal("MSG-TRACE-05", row.MessageCode);

        var shortRow = service.Queue(Guid.NewGuid(), "999900000001", Content with { Message = UserMessages.ProvenanceWithdrawnNotice("DEMO HTX", "LOT-DEMO-01", ["HD000001"], null) });
        Assert.Contains("HD000001", shortRow.MessageParams);
    }

    // QA M2: the rows of one save are handed to the service at the same time, and saved once after every send answered.
    [Fact]
    public async Task Several_messages_are_sent_at_the_same_time_and_saved_once()
    {
        var running = 0;
        var most = 0;
        var (service, stubs) = Build(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Interlocked.Decrement(ref running);
            return DeliveryResult.Sent("id");
        });
        var rows = Enumerable.Range(0, 4).Select(_ => service.Queue(Guid.NewGuid(), "999900000001", Content)).ToList();

        await service.DeliverAllAsync(rows);

        Assert.Equal(4, most);
        Assert.All(rows, r => Assert.Equal(StatusIds.MessageDelivery.Sent, r.DeliveryStatusId));
        Assert.Equal(1, stubs.Saves);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact]
    public async Task A_second_save_that_fails_changes_no_answer()
    {
        var (service, stubs) = Build(_ => Task.FromResult(DeliveryResult.Sent("id")));
        stubs.SaveFails = true;
        var row = service.Queue(Guid.NewGuid(), "0900000099", Content);

        await service.DeliverAsync(row);

        Assert.Equal(1, stubs.Saves);
    }

    private sealed class Stubs(Func<CancellationToken, Task<DeliveryResult>> send) : ITraderMessageSender, INotificationRepository, IUnitOfWork
    {
        public Notification? Added { get; private set; }
        public int Saves { get; private set; }
        public bool SaveFails { get; set; }

        public NotificationChannel Channel => NotificationChannel.Sms;

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default) => send(cancellationToken);

        public void Add(Notification notification) => Added = notification;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return SaveFails ? throw new InvalidOperationException("database down") : Task.FromResult(1);
        }

        public void DiscardChanges() => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<NotificationPreference>>> GetPreferencesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(Guid recipientAccountId, bool unreadOnly, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Notification?> GetAsync(Guid recipientAccountId, Guid notificationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Notification>> ListUnreadAsync(Guid recipientAccountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => throw new NotSupportedException();

        public void MarkChanged(AuditableEntity entity) => throw new NotSupportedException();
    }
}
