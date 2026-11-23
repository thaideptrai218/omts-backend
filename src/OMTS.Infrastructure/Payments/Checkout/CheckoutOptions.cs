using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// "Checkout" section: the trader payments of the Zalo Mini App (decision of 21/11/2026), Zalo's Checkout SDK with payOS as the third party. <c>PayOS</c> calls payOS
/// and Zalo, and needs every key below; <c>Fake</c> works in Development only (no network and no key needed: payOS links are made up and the reports to Zalo are kept in
/// memory; the signatures use the keys below when they are set) and elsewhere acts as unset; unset (the default) answers MSG04 to every checkout, so a deployment
/// without the keys never takes money.
/// The keys and the private key are secrets: user-secrets or environment variables only.
/// </summary>
public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";
    public const string PayOsProvider = "PayOS";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = "";

    /// <summary>The Mini App ID (Mini App Center).</summary>
    public string ZaloMiniAppId { get; set; } = "";

    /// <summary>The private key of Mini App Center → Checkout SDK → Cấu hình chung; signs the orders and the reports.</summary>
    public string ZaloPrivateKey { get; set; } = "";

    /// <summary>The id of the custom payment method declared in Mini App Center (the payOS QR code).</summary>
    public string ZaloCustomMethodId { get; set; } = "";

    public string ZaloPaymentBaseUrl { get; set; } = "https://payment-mini.zalo.me/";

    /// <summary>my.payos.vn → Kênh thanh toán.</summary>
    public string PayOsClientId { get; set; } = "";

    public string PayOsApiKey { get; set; } = "";

    /// <summary>Signs the payment links and checks the notifications.</summary>
    public string PayOsChecksumKey { get; set; } = "";

    public string PayOsBaseUrl { get; set; } = "https://api-merchant.payos.vn/";

    /// <summary>
    /// The bank (BIN, 6 digits) and the number of the account linked to the payOS channel; the cash book needs a Bank account with the same two. Empty: the
    /// cooperative's receiving bank account (DEBT-01) is taken.
    /// </summary>
    public string PayOsBankBin { get; set; } = "";

    public string PayOsAccountNumber { get; set; } = "";

    /// <summary>The return and cancel URL payOS requires for a link; the Mini App never opens the payOS page. Empty: the deeplink of the Mini App.</summary>
    public string ReturnUrl { get; set; } = "";

    /// <summary>After this a payOS or Zalo call is abandoned: the checkout answers MSG04, a report is sent again later. 1 to 120.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>The job that sends again the reports Zalo did not accept; tests switch it off and call it directly.</summary>
    public bool ReportRetryEnabled { get; set; } = true;

    public int ReportRetrySeconds { get; set; } = 60;

    internal bool HasZaloKeys =>
        !string.IsNullOrWhiteSpace(ZaloMiniAppId) && !string.IsNullOrWhiteSpace(ZaloPrivateKey) && !string.IsNullOrWhiteSpace(ZaloCustomMethodId);

    internal bool HasPayOsKeys =>
        !string.IsNullOrWhiteSpace(PayOsClientId) && !string.IsNullOrWhiteSpace(PayOsApiKey) && !string.IsNullOrWhiteSpace(PayOsChecksumKey);

    internal PayOsAccount? LinkedAccount =>
        string.IsNullOrWhiteSpace(PayOsBankBin) || string.IsNullOrWhiteSpace(PayOsAccountNumber) ? null : new PayOsAccount(PayOsBankBin.Trim(), PayOsAccountNumber.Trim());

    internal string ReturnUrlOrDeeplink => string.IsNullOrWhiteSpace(ReturnUrl) ? $"https://zalo.me/s/{ZaloMiniAppId.Trim()}/" : ReturnUrl.Trim();
}
