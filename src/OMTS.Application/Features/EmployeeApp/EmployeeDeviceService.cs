using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>The push token of the linked device of an employee (BR-LABOR-04): where the pushes of the employee app are delivered.</summary>
public sealed class EmployeeDeviceService(IEmployeeAppRepository app, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    private const int MaxTokenLength = 500;

    /// <summary>Sets the token of the caller's own device; an empty token clears it. The device is the one named by the caller's access token, never another.</summary>
    public async Task<Result<Done>> SetPushTokenAsync(Guid employeeId, Guid deviceId, EmployeePushTokenRequest request, CancellationToken cancellationToken)
    {
        var token = request.PushToken?.Trim();
        if (token is { Length: > MaxTokenLength })
        {
            return AppError.Invalid("Push token");
        }

        if (await app.GetDeviceAsync(deviceId, cancellationToken) is not { IsLinked: true } device || device.EmployeeId != employeeId)
        {
            return AppError.NotFound;
        }

        device.PushToken = string.IsNullOrEmpty(token) ? null : token;
        device.PushPermissionGranted = device.PushToken is not null && request.PushPermissionGranted;
        device.LastSeenAt = timeProvider.GetUtcNow();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }
}
