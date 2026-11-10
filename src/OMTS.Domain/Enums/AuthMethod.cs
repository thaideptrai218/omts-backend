namespace OMTS.Domain.Enums;

/// <summary>How the person proved who they are when a login session was opened.</summary>
public enum AuthMethod
{
    /// <summary>Username or phone number with a password.</summary>
    Password,

    /// <summary>Zalo Mini App: the Zalo account linked to the OMTS account (no password on mobile).</summary>
    ZaloMiniApp,
}
