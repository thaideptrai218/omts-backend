using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Accounts;

/// <param name="PermissionGranted">False when the user declined notifications on the device.</param>
public sealed record RegisterPushDeviceRequest(string Token, string Platform, bool PermissionGranted);

/// <summary>ACS-07 – registers the Expo push token of the device the user is logged in on.</summary>
public sealed class PushDeviceService(IPushDeviceRepository devices, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    private static readonly string[] Platforms = ["ios", "android", "web"];

    /// <summary>Re-registering a token updates it; a token registered by another account moves to this one.</summary>
    public async Task<Result<Done>> RegisterAsync(Guid accountId, RegisterPushDeviceRequest request, CancellationToken cancellationToken)
    {
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 500)
        {
            return AppError.Invalid("Push token");
        }

        var platform = request.Platform?.Trim().ToLowerInvariant();
        if (platform is null || !Platforms.Contains(platform))
        {
            return AppError.Invalid("Platform");
        }

        var now = timeProvider.GetUtcNow();
        var device = await devices.GetByTokenAsync(token, cancellationToken);
        if (device is null)
        {
            devices.Add(new PushDevice { AccountId = accountId, Token = token, Platform = platform, PermissionGranted = request.PermissionGranted, RegisteredAt = now, LastSeenAt = now });
        }
        else
        {
            device.AccountId = accountId;
            device.Platform = platform;
            device.PermissionGranted = request.PermissionGranted;
            device.LastSeenAt = now;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>Removes the device (e.g. at logout on the phone); unknown tokens or other accounts' tokens are ignored.</summary>
    public async Task UnregisterAsync(Guid accountId, string token, CancellationToken cancellationToken)
    {
        if (await devices.GetByTokenAsync(token.Trim(), cancellationToken) is { } device && device.AccountId == accountId)
        {
            devices.Remove(device);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
