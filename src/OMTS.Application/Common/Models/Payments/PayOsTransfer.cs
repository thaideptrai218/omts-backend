namespace OMTS.Application.Common.Models.Payments;

/// <summary>A transfer payOS reported in a notification whose signature was checked.</summary>
/// <param name="Successful">payOS reported the payment as made (its <c>code</c> "00"); anything else records nothing.</param>
/// <param name="OrderCode">The order code of the payment link.</param>
/// <param name="Amount">What arrived, in VND.</param>
/// <param name="Reference">The bank reference of the transfer; empty when payOS sent none.</param>
/// <param name="OccurredAt">When the money moved; null when payOS sent no time that can be read.</param>
public sealed record PayOsTransfer(bool Successful, long OrderCode, decimal Amount, string Reference, string? Description, DateTimeOffset? OccurredAt);
