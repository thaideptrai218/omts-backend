namespace OMTS.Application.Common.Models.Payments;

/// <summary>
/// What a payOS payment link is made of. Only the order code, the amount and the transfer content leave OMTS: nothing about the trader (CLAUDE.md §6, no personal data
/// from Zalo to a third party).
/// </summary>
/// <param name="OrderCode">Unique per payOS channel; the notification names the link by it.</param>
/// <param name="Amount">Whole VND.</param>
/// <param name="Description">The transfer content: the code of the payment (at most 9 characters for a bank not linked through payOS).</param>
/// <param name="ExpiresAt">After this payOS refuses the payment.</param>
public sealed record PayOsLinkRequest(long OrderCode, long Amount, string Description, DateTimeOffset ExpiresAt);
