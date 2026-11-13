using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Technical entity (SDS, BR-LABOR-02): the device on which an employee entered a correct login code. An employee has one
/// linked device (a filtered unique index); linking another unlinks this one and ends its sessions. The device proves itself
/// with a random secret given once at linking, stored only as a hash, so a login on it needs no code until it is unlinked.
/// Only the linked device shows the attendance QR code (LABOR-07).
/// </summary>
public class EmployeeDevice : Entity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>SHA-256 of a 256-bit random value; a random value of that size needs no slow hash.</summary>
    public string DeviceSecretHash { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the secret that <see cref="DeviceSecretHash"/> replaced at the last code-less login. The secret is rotated at every such login, so
    /// a login that presents this one again comes from a copy of the app: the device is unlinked (clone detection).
    /// </summary>
    public string? PreviousDeviceSecretHash { get; set; }

    /// <summary>"ios" or "android".</summary>
    public string Platform { get; set; } = string.Empty;

    public string? DeviceName { get; set; }

    /// <summary>Where the employee push goes; null when none was registered, and cleared when the device is unlinked.</summary>
    public string? PushToken { get; set; }

    public bool PushPermissionGranted { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>The login code whose entry linked the device.</summary>
    public Guid LinkedByLoginCodeId { get; set; }
    public EmployeeLoginCode LinkedByLoginCode { get; set; } = null!;

    public DateTimeOffset? UnlinkedAt { get; set; }
    public EmployeeDeviceUnlinkReason? UnlinkReason { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The last attendance QR step a scan used on this device, so a QR code photographed earlier cannot be scanned again (LABOR-07, L5).</summary>
    public long LastQrStep { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public bool IsLinked => UnlinkedAt is null;

    /// <summary>Replaces the secret with a new one's hash, keeping the one replaced so its return can be told from a wrong guess.</summary>
    public void RotateSecret(string newSecretHash)
    {
        PreviousDeviceSecretHash = DeviceSecretHash;
        DeviceSecretHash = newSecretHash;
    }

    public void Unlink(DateTimeOffset now, EmployeeDeviceUnlinkReason reason)
    {
        if (UnlinkedAt is not null)
        {
            return;
        }

        UnlinkedAt = now;
        UnlinkReason = reason;
        PushToken = null;
        PushPermissionGranted = false;
    }
}
