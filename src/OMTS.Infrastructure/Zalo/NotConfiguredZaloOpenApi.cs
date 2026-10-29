using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Zalo;

/// <summary>Default when <c>Zalo:Provider</c> is unset: nothing is checked, linking answers MSG04.</summary>
public sealed class NotConfiguredZaloOpenApi : IZaloOpenApi
{
    public Task<ZaloLocationLookup> GetLocationAsync(string accessToken, string locationToken, CancellationToken cancellationToken) =>
        Task.FromResult(ZaloLocationLookup.Unavailable);

    public Task<ZaloLookup> GetUserIdAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(ZaloLookup.Unavailable);

    public Task<ZaloLookup> GetPhoneNumberAsync(string accessToken, string phoneToken, CancellationToken cancellationToken) =>
        Task.FromResult(ZaloLookup.Unavailable);
}
