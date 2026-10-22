namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// A bank account: the one the cooperative receives transfers on (owned by <see cref="Cooperative"/>, PAY-01)
/// or the one an employee's wage is paid to (owned by <see cref="Employee"/>, LAB-01).
/// </summary>
public class BankAccountInfo
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
}
