namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>Money into the account, or out of it (the services report both; only money in is a customer payment).</summary>
public enum BankTransferDirection
{
    In,
    Out,
}

/// <summary>
/// One transfer as the Payment Notification Service reports it (API-13), whatever the provider's format: what GFR-08 records. Read by an <see cref="IBankNotificationParser"/>.
/// </summary>
/// <param name="BankReference">The reference of the transfer at the bank: a transfer reported again with it is not recorded twice.</param>
/// <param name="AccountNumber">The account that received (or sent) the money: only the receiving bank account counts (BR-DEBT-01).</param>
/// <param name="Amount">VND.</param>
/// <param name="Content">The transfer content, which holds the invoice code of a VietQR code (BR-SALE-07 (1)).</param>
/// <param name="OccurredAt">The time of the transfer at the bank.</param>
public sealed record BankTransferNotice(string BankReference, string AccountNumber, decimal Amount, string? Content, DateTimeOffset OccurredAt, BankTransferDirection Direction);
