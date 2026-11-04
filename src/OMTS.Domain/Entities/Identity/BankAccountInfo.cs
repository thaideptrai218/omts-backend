namespace OMTS.Domain.Entities.Identity;

/// <summary>The bank account an employee's wage is paid to (owned by <see cref="Employee"/>, LAB-01).</summary>
public class BankAccountInfo
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
}
