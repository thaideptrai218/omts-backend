using System.ComponentModel.DataAnnotations;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="ZaloAccessToken">From <c>getAccessToken()</c> of the Zalo Mini App.</param>
/// <param name="PhoneToken">From <c>getPhoneNumber()</c>; works once and for about two minutes.</param>
public sealed record ZaloLinkRequest(
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string ZaloAccessToken,
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string PhoneToken,
    ClientType ClientType = ClientType.Mobile,
    string? DeviceName = null)
{
    // Anonymous input sent on to Zalo: bounded first. Zalo tokens are a few hundred characters.
    public const int MaxTokenLength = 2048;
}
