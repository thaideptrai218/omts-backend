using OMTS.Domain.Enums;

namespace OMTS.Application.Features.EmployeeApp;

/// <summary>
/// POST /api/employee-app/login (LABOR-06). <paramref name="DeviceId"/> and <paramref name="DeviceSecret"/> are what the app got when
/// its device was linked: with them a device that is already linked logs in without a code (LABOR-06.1). Without them the code is needed
/// and the device on which it is entered becomes the linked one.
/// </summary>
/// <param name="PhoneNumber">The phone number of the profile, in any usual spelling.</param>
/// <param name="LoginCode">The 6 digits the Zone Manager or the Cooperative Owner issued (LABOR-03).</param>
/// <param name="Platform">"ios" or "android"; read when a device is linked.</param>
/// <param name="DeviceName">Optional name of the phone, shown to the Zone Manager (e.g. "Pixel 7").</param>
public sealed record EmployeeAppLoginRequest(
    string? PhoneNumber, Guid? DeviceId, string? DeviceSecret, string? LoginCode, string? Platform, string? DeviceName);

public sealed record EmployeeAppRefreshRequest(string? RefreshToken);

public sealed record EmployeeAppProfile(Guid Id, string FullName, EmployeeType? Type);

/// <summary>Login and refresh (BR-LABOR-02).</summary>
/// <param name="RefreshTokenRotated">As for a staff login: false when the token its last refresh replaced came back inside the grace period, so the client keeps the one it holds.</param>
/// <param name="DeviceSecret">Only in the answer that linked the device: the app keeps it to log in again without a code; it is never shown again.</param>
/// <param name="QrKey">The key the app signs the attendance QR code of this device with (LABOR-07); the same at every login of the device.</param>
/// <param name="QrStepSeconds">How long one QR code lasts before the app replaces it (BR-LABOR-06).</param>
/// <param name="ServerTime">So the app can keep its QR clock in step with the server's, offline.</param>
public sealed record EmployeeAppTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool RefreshTokenRotated,
    EmployeeAppProfile Employee,
    Guid DeviceId,
    string? DeviceSecret,
    string QrKey,
    int QrStepSeconds,
    DateTimeOffset ServerTime);

/// <param name="Code">The 6 digits, shown here and in the issuer's notification; stored nowhere else, and only as a hash.</param>
/// <param name="Message">MSG-LABOR-08.</param>
public sealed record EmployeeLoginCodeIssued(
    string Code, DateTimeOffset ExpiresAt, string PhoneNumber, string EmployeeName, string Message, string MessageCode);

/// <param name="PushToken">The Expo push token of the phone; empty clears it.</param>
/// <param name="PushPermissionGranted">False when the employee declined notifications on the device.</param>
public sealed record EmployeePushTokenRequest(string? PushToken, bool PushPermissionGranted);
