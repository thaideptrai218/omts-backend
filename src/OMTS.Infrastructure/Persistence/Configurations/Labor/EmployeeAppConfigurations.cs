namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

/// <summary>Names of the unique indexes of the employee app tables (BR-LABOR-02), so the configurations and the SQL Server tests name the same ones.</summary>
public static class EmployeeAppConfigurations
{
    /// <summary>At most one open login code per employee: a new code cancels the earlier one.</summary>
    public const string OneOpenLoginCodePerEmployee = "UX_EmployeeLoginCodes_OneOpenPerEmployee";

    /// <summary>At most one linked device per employee: linking another unlinks the first.</summary>
    public const string OneLinkedDevicePerEmployee = "UX_EmployeeDevices_OneLinkedPerEmployee";
}
