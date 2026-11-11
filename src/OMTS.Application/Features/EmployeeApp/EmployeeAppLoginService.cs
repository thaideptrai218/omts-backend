using System.Security.Cryptography;
using System.Text;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>
/// LABOR-06 – log in to the employee app with the phone number of the profile (BR-LABOR-02). The linked device logs in with its
/// secret and no code; any other device needs the login code a Zone Manager or the Owner issued, and the device on which a correct
/// code is entered becomes the linked one, so the device linked before is logged out. The code is checked against its hash only.
/// </summary>
public sealed class EmployeeAppLoginService(
    IEmployeeAppRepository app,
    IVerificationCodeHasher codeHasher,
    EmployeeAppAccess access,
    EmployeeAppTokenIssuer issuer,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // Parallel wrong entries of one code conflict on its row version; each round saves one of them, so this covers any burst the per-IP limit lets through.
    private const int MaxSaveAttempts = 20;

    private const int MaxDeviceNameLength = 200;

    private static readonly string[] Platforms = ["ios", "android"];

    public async Task<Result<EmployeeAppTokens>> LoginAsync(EmployeeAppLoginRequest request, CancellationToken cancellationToken)
    {
        var phone = PhoneNumbers.Normalize(request.PhoneNumber);
        if (phone is null)
        {
            return AppError.Invalid("Phone number");
        }

        var today = timeProvider.Today();
        // LABOR-06.0.E1: no Employed employee has the number. The SRS wants this said, so the number is not a secret; the per-IP limit slows a scan of numbers.
        var employee = await app.FindEmployedByPhoneAsync(phone, today, cancellationToken);
        if (employee is null)
        {
            return new AppError(ErrorKind.Unauthorized, UserMessages.EmployeePhoneNotRegistered);
        }

        if (request.DeviceId is { } deviceId && !string.IsNullOrEmpty(request.DeviceSecret)
            && await LoginOnLinkedDeviceAsync(employee, deviceId, request.DeviceSecret, cancellationToken) is { } relogin)
        {
            return relogin;
        }

        if (string.IsNullOrWhiteSpace(request.LoginCode))
        {
            // LABOR-06 step 4: this device is not linked yet.
            return new AppError(
                ErrorKind.Unauthorized, UserMessages.DeviceNotLinked, new Dictionary<string, object?> { ["loginCodeRequired"] = true });
        }

        var platform = request.Platform?.Trim().ToLowerInvariant();
        if (platform is null || !Platforms.Contains(platform))
        {
            // Before the code is looked at: a request that cannot link a device must not use up a try of the code.
            return AppError.Invalid("Platform");
        }

        return await LoginWithCodeAsync(employee, request.LoginCode.Trim(), platform, request.DeviceName, cancellationToken);
    }

    /// <summary>LABOR-06.1: the linked device with its secret; null when this is not the linked device, so the code is asked for.</summary>
    private async Task<Result<EmployeeAppTokens>?> LoginOnLinkedDeviceAsync(
        Employee employee, Guid deviceId, string deviceSecret, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var device = await app.GetLinkedDeviceAsync(employee.Id, cancellationToken);
                if (device is null || device.Id != deviceId
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(SecretHashing.Sha256Hex(deviceSecret)), Encoding.ASCII.GetBytes(device.DeviceSecretHash)))
                {
                    return null;
                }

                var now = timeProvider.GetUtcNow();
                device.LastSeenAt = now;
                var (session, refreshToken) = issuer.OpenSession(employee.Id, device.Id, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return issuer.Tokens(employee, session, device.Id, refreshToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                // The device changed meanwhile (unlinked, or another login): read it again.
                unitOfWork.DiscardChanges();
            }
        }
    }

    private async Task<Result<EmployeeAppTokens>> LoginWithCodeAsync(
        Employee employee, string loginCode, string platform, string? deviceName, CancellationToken cancellationToken)
    {
        EmployeeLoginCode code;
        var now = timeProvider.GetUtcNow();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                now = timeProvider.GetUtcNow();
                var open = (await app.ListOpenCodesAsync(employee.Id, cancellationToken)).OrderByDescending(c => c.IssuedAt).FirstOrDefault();
                if (open is null || !open.IsUsable(now))
                {
                    return Rejected();
                }

                if (!codeHasher.Matches(open.Id, loginCode, open.CodeHash))
                {
                    // The 5th wrong entry cancels the code (BR-LABOR-02).
                    open.FailedAttempts++;
                    if (open.IsLocked)
                    {
                        open.CancelledAt = now;
                    }

                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return Rejected();
                }

                // First save: the code is spent and the device linked before is out. The new device goes in the second save, because a
                // filtered unique index (one linked device) is checked statement by statement and the order of one save is not promised.
                open.ConsumedAt = now;
                await access.RevokeAsync(employee.Id, EmployeeDeviceUnlinkReason.NewDevice, cancellationToken, actorEmployeeId: employee.Id);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                code = open;
                break;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxSaveAttempts)
            {
                // Another entry of the same code or another login got in first: read the code again, it may be spent or cancelled now.
                unitOfWork.DiscardChanges();
            }
        }

        // 256 random bits as 64 hex characters; only their SHA-256 is stored.
        var deviceSecret = SecretHashing.NewToken();
        var device = new EmployeeDevice
        {
            EmployeeId = employee.Id,
            DeviceSecretHash = SecretHashing.Sha256Hex(deviceSecret),
            Platform = platform,
            DeviceName = deviceName is { Length: > MaxDeviceNameLength } ? deviceName[..MaxDeviceNameLength] : deviceName,
            LinkedAt = now,
            LinkedByLoginCodeId = code.Id,
            LastSeenAt = now,
        };
        app.Add(device);
        var (session, refreshToken) = issuer.OpenSession(employee.Id, device.Id, now);
        audit.Record(new AuditEntry("Employee.DeviceLinked", nameof(Employee))
        {
            RecordId = employee.Id,
            After = new { device.Platform, device.DeviceName },
            ActorEmployeeId = employee.Id,
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A login with a code issued meanwhile linked another device between the two saves: this code is spent, ask for a new one.
            unitOfWork.DiscardChanges();
            return Rejected();
        }

        return issuer.Tokens(employee, session, device.Id, refreshToken, deviceSecret: deviceSecret);
    }

    // MSG-LABOR-19: a wrong, expired, spent or cancelled code all answer the same, so the answer says nothing about which.
    private static Result<EmployeeAppTokens> Rejected() => new AppError(ErrorKind.Unauthorized, UserMessages.LoginCodeRejected);
}
