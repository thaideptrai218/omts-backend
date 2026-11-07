namespace OMTS.Infrastructure.Persistence.Configurations;

internal static class Lengths
{
    public const int Code = 50;
    public const int ShortText = 100;

    /// <summary>A zone code, a raft code, the date and a suffix (BR-HARVEST-03). The audit log and the notifications copy it, so their record code columns hold as much.</summary>
    public const int LotCode = 120;

    public const int Name = 200;
    public const int Address = 500;
    public const int Phone = 20;
    public const int Email = 256;
    public const int Url = 1000;
    public const int Reason = 1000;
    public const int LongText = 2000;
    public const int Hash = 128;
}
