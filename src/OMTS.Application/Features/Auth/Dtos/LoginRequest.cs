using System.ComponentModel.DataAnnotations;
using OMTS.Application.Common.Validation;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="Identifier">Phone number (verified or not: the password proves the identity) or username (UC02). An email address never matches.</param>
/// <param name="Password">At most 128 characters, the longest password the password rule allows.</param>
/// <param name="DeviceId">Random id of this browser or app install, base64url, at most 128 characters; missing, empty or <c>unknown</c> means no device.</param>
public sealed record LoginRequest(
    [Required, MaxLength(LoginRequest.MaxIdentifierLength)] string Identifier,
    [Required, MaxLength(LoginRequest.MaxPasswordLength)] string Password,
    ClientType ClientType = ClientType.Web,
    string? DeviceName = null,
    [MaxLength(DeviceIds.MaxLength), RegularExpression(DeviceIds.AllowedPattern)] string? DeviceId = null)
{
    // Anonymous input: bounded before it is hashed, sent to SQL or used as a cache key. 128 is the password
    // rule's maximum (ACS shared rules).
    public const int MaxIdentifierLength = 254;
    public const int MaxPasswordLength = 128;
}
