namespace OMTS.Domain.Enums;

/// <summary>How a time of an attendance record, or a job period, was recorded (BR-LABOR-06).</summary>
public enum AttendanceMethod
{
    /// <summary>The Zone Manager scanned the attendance QR code of the employee.</summary>
    QrCode,

    /// <summary>The Zone Manager marked it by hand.</summary>
    Manual,

    /// <summary>The start time of a crew, entered with a reason because the Zone Manager was not at the raft (LABOR-04.8).</summary>
    CrewStart,

    /// <summary>Applied by the Cooperative Owner from an approved employee request (LABOR-05).</summary>
    Request,

    /// <summary>Entered by the Cooperative Owner with a reason (LABOR-04.4).</summary>
    Correction,
}
