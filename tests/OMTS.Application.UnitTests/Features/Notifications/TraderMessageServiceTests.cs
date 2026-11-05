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
