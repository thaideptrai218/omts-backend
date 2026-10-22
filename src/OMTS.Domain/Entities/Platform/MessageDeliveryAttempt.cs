using OMTS.Domain.Common;
using OMTS.Domain.Enums;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E47 – One attempt to deliver a document to a trader. Exactly one of the five source document keys
/// is set (BR-26); a retry keeps the same source and customer.
/// </summary>
public class MessageDeliveryAttempt : Entity, IHasStatus
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Empty when sent automatically (JOB-03, automatic notices).</summary>
    public Guid? SentByAccountId { get; set; }
    public Account? SentByAccount { get; set; }

    public Guid? OrderId { get; set; }
    public SalesOrder? Order { get; set; }

    public Guid? SaleId { get; set; }
    public SaleTransaction? Sale { get; set; }

    public Guid? AdjustmentId { get; set; }
    public SaleAdjustment? Adjustment { get; set; }

    public Guid? PublicationId { get; set; }
    public TracePublicationVersion? Publication { get; set; }

    public Guid? ReceiptId { get; set; }
    public CustomerReceipt? Receipt { get; set; }

    public Guid? RetryOfAttemptId { get; set; }
    public MessageDeliveryAttempt? RetryOfAttempt { get; set; }

    /// <summary>Email address, Zalo account or phone number.</summary>
    public string Recipient { get; set; } = string.Empty;
    public MessageChannel Channel { get; set; }

    public string DocumentType { get; set; } = string.Empty;
    public string? DocumentVersion { get; set; }

    /// <summary>Snapshot of the exact content sent.</summary>
    public string? ContentSnapshot { get; set; }

    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderResponseCode { get; set; }

    public int StatusId { get; set; } = StatusIds.MessageDelivery.Sending;
    public Status Status { get; set; } = null!;
}
