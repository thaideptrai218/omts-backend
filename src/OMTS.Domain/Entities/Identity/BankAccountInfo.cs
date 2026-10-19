namespace OMTS.Domain.Entities.Identity;

/// <summary>Bank account the cooperative receives transfers on (owned by <see cref="Cooperative"/>).</summary>
public class BankAccountInfo
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
}
