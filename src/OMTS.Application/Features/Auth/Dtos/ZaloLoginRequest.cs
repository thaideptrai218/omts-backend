using System.ComponentModel.DataAnnotations;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="ZaloAccessToken">From <c>getAccessToken()</c> of the Zalo Mini App.</param>
public sealed record ZaloLoginRequest(
    [Required, MaxLength(ZaloLinkRequest.MaxTokenLength)] string ZaloAccessToken,
    ClientType ClientType = ClientType.Mobile,
    string? DeviceName = null);
