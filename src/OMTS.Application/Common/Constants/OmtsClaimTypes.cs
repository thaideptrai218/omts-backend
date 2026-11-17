namespace OMTS.Application.Common.Constants;

/// <summary>Claim names carried by OMTS access tokens.</summary>
public static class OmtsClaimTypes
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string TokenId = "jti";
    public const string Name = "name";
    public const string SubjectType = "subject_type";

    /// <summary>
    /// Claims of an employee app token (BR-LABOR-02). Such a token has no <see cref="Subject"/> and no <see cref="SessionId"/>:
    /// it names no account, so nothing that reads the staff claims can take it for a user.
    /// </summary>
    public const string EmployeeId = "employee_id";

    public const string EmployeeSessionId = "employee_session_id";
    public const string EmployeeDeviceId = "employee_device_id";

    /// <summary>The value of <see cref="SubjectType"/> in an employee app token; no staff <c>SubjectType</c> has this name.</summary>
    public const string EmployeeSubjectType = "Employee";
}
