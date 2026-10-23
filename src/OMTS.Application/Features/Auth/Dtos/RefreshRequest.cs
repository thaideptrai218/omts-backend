using System.ComponentModel.DataAnnotations;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="RefreshToken">Issued tokens are well under 512 characters; longer input is refused before hashing.</param>
public sealed record RefreshRequest([Required, MaxLength(512)] string RefreshToken);
