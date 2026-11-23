namespace OMTS.Application.Features.Sales.Checkout.Dtos;

/// <summary>
/// The QR code of the payOS link, which the Mini App draws itself (it never opens the payOS page), and the details to copy when the phone cannot scan its own screen.
/// </summary>
/// <param name="Content">The VietQR text.</param>
/// <param name="TransferContent">What the transfer must carry: the code of the payment.</param>
public sealed record TraderCheckoutQr(string Content, string BankBin, string AccountNumber, string AccountName, decimal Amount, string TransferContent);
