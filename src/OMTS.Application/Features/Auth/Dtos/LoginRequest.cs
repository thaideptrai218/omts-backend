using System.ComponentModel.DataAnnotations;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="Identifier">Username, verified phone number or verified email (UC02).</param>
/// <param name="Password">At most 128 characters, the longest password the password rule allows.</param>
public sealed record LoginRequest(
    [Required, MaxLength(LoginRequest.MaxIdentifierLength)] string Identifier,
    [Required, MaxLength(LoginRequest.MaxPasswordLength)] string Password,
    ClientType ClientType = ClientType.Web,
    string? DeviceName = null)
{
    // Anonymous input: bounded before it is hashed, sent to SQL or used as a cache key. 254 is the longest
    // email address; 128 is the password rule's maximum (ACS shared rules).
    public const int MaxIdentifierLength = 254;
    public const int MaxPasswordLength = 128;
}
