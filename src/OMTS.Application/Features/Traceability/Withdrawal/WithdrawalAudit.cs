using System.Text.Json;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Withdrawal;

/// <summary>One trader told of a withdrawal, as the audit row keeps it: also how the notices of the latest withdrawal are read again (TRACE-04 step 7).</summary>
/// <param name="SendingId">The Notification row of the sending to the trader's phone, with its delivery status.</param>
/// <param name="NotifiedInApp">A list row was written for the trader's active account (TRACE-04.1).</param>
public sealed record WithdrawalAuditTrader(Guid CustomerId, string CustomerName, IReadOnlyList<string> InvoiceCodes, Guid SendingId, bool NotifiedInApp);

/// <summary>The after value of the audit row <c>HarvestLot.Withdrawn</c> (GBR-02).</summary>
public sealed record WithdrawalAuditAfter(
    string OriginState, DateTimeOffset? WithdrawnAt, IReadOnlyList<WithdrawalAuditTrader> NotifiedTraders, IReadOnlyList<string> WalkInInvoiceCodes);

/// <summary>Writes and reads the trader list of the withdrawal audit row, and turns one trader with its sending into the notice the Cooperative Owner sees.</summary>
public static class WithdrawalAudit
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static WithdrawalAuditTrader TraderOf(QueuedWithdrawalNotice queued) => new(
        queued.Trader.CustomerId, queued.Trader.CustomerName, queued.Trader.InvoiceCodes, queued.Sending.Id, queued.NotifiedInApp);

    /// <summary>The stored after value; null when there is none or it is not one of a withdrawal.</summary>
    public static WithdrawalAuditAfter? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WithdrawalAuditAfter>(json, ReadOptions) is { NotifiedTraders: not null, WalkInInvoiceCodes: not null } after ? after : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The notice of one trader with the delivery status of its sending now: a Sending row left by a stop reads Not sent (<see cref="TraderMessageService.EffectiveStatus"/>), and a
    /// trader whose sending is missing is to call.
    /// </summary>
    public static WithdrawalNotice NoticeOf(WithdrawalAuditTrader trader, Notification? sending, IStatusCatalog statusCatalog, DateTimeOffset now)
    {
        var status = sending is null
            ? StatusIds.MessageDelivery.NotSent
            : TraderMessageService.EffectiveStatus(sending.DeliveryStatusId, sending.DeliveryStatusAt, now);
        return new WithdrawalNotice(
            trader.CustomerId, trader.CustomerName, sending?.PhoneNumber, sending?.RecipientAccountId, trader.InvoiceCodes, sending?.Channel ?? NotificationChannel.Sms, status,
            statusCatalog.DisplayNameOf(status), trader.NotifiedInApp, status == StatusIds.MessageDelivery.NotSent);
    }
}
