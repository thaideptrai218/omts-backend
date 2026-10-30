using System.ComponentModel.DataAnnotations;

namespace OMTS.Application.Features.Auth.Dtos;

/// <summary>A trader without an account asks the cooperative for one from the Zalo Mini App.</summary>
/// <param name="ZaloAccessToken">From <c>getAccessToken()</c> of the Zalo Mini App.</param>
/// <param name="PhoneToken">A new token from <c>getPhoneNumber()</c>: a phone token works only once.</param>
/// <param name="DisplayName">The name the Cooperative Owner sees on the request.</param>
/// <param name="Note">Optional words to the cooperative, e.g. where the trader buys oysters.</param>
public sealed record ZaloTraderRequest(
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string ZaloAccessToken,
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string PhoneToken,
    string? DisplayName,
    string? Note)
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxNoteLength = 500;
}
