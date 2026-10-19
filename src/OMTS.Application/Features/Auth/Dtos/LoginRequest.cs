using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="Identifier">Username, verified phone number or verified email (UC02).</param>
public sealed record LoginRequest(string Identifier, string Password, ClientType ClientType = ClientType.Web, string? DeviceName = null);
