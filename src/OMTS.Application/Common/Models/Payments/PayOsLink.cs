namespace OMTS.Application.Common.Models.Payments;

/// <summary>A payOS payment link as payOS made it: the QR text the Mini App draws and the account details the trader may copy instead.</summary>
/// <param name="PaymentLinkId">The id payOS gave the link.</param>
/// <param name="QrContent">The VietQR text of the link.</param>
/// <param name="Description">The transfer content payOS put in the QR code.</param>
public sealed record PayOsLink(string PaymentLinkId, string QrContent, string BankBin, string AccountNumber, string AccountName, string Description);
