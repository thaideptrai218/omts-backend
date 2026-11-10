using System.ComponentModel.DataAnnotations;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="ZaloAccessToken">From <c>getAccessToken()</c> of the Zalo Mini App.</param>
/// <param name="PhoneToken">From <c>getPhoneNumber()</c>; works once and for about two minutes.</param>
/// <param name="DeviceId">Random id of this app install, base64url, at most 128 characters; missing, empty or <c>unknown</c> means no device.</param>
public sealed record ZaloLinkRequest(
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string ZaloAccessToken,
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string PhoneToken,
    ClientType ClientType = ClientType.Mobile,
    string? DeviceName = null,
    [MaxLength(DeviceIds.MaxLength), RegularExpression(DeviceIds.AllowedPattern)] string? DeviceId = null)
{
    // Anonymous input sent on to Zalo: bounded first. Zalo tokens are a few hundred characters.
    public const int MaxTokenLength = 2048;
}
