namespace OMTS.Domain.Enums;

/// <summary>Client that opened a login session; decides refresh lifetimes (NFR-01).</summary>
public enum ClientType
{
    Web,
    Mobile,
}
