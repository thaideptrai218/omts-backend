namespace OMTS.Domain.Constants;

/// <summary>Fixed primary keys of the seeded VAT rates, so code and migrations can point at them and every environment holds the same rows.</summary>
public static class VatRateIds
{
    public static readonly Guid Kct = new("7a1c4e20-0000-4b7a-8c10-000000000001");
    public static readonly Guid Zero = new("7a1c4e20-0000-4b7a-8c10-000000000002");
    public static readonly Guid Five = new("7a1c4e20-0000-4b7a-8c10-000000000003");
    public static readonly Guid Eight = new("7a1c4e20-0000-4b7a-8c10-000000000004");
    public static readonly Guid Ten = new("7a1c4e20-0000-4b7a-8c10-000000000005");
}
