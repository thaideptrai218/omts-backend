namespace OMTS.Domain.Enums;

/// <summary>Where a sale paid with the VietQR code of its invoice stands while the transfer is awaited (SALE-05 steps 7-8, SALE-05.0.E4).</summary>
public enum PendingSaleStatus
{
    /// <summary>The VietQR code is shown and the transfer has not arrived: nothing is saved yet.</summary>
    Awaiting,

    /// <summary>The Payment Notification Service reported the transfer and the invoice was saved Completed (GFR-08).</summary>
    Completed,

    /// <summary>The Zone Manager changed the payment method and the invoice was saved with the same code (SALE-05.0.E4 step 2).</summary>
    PaymentChanged,

    /// <summary>The Zone Manager stopped the sale: nothing is saved and the code is in the audit log (SALE-05.0.E4 step 3).</summary>
    Stopped,
}
