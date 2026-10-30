
namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>Which login branch a demo staff account is meant to land in.</summary>
internal enum DemoLoginKind
{
    /// <summary>Verified phone, own password: goes straight in.</summary>
    Normal,

    /// <summary>Temporary password that never expires in the demo: must change it at first login.</summary>
    MustChangePassword,

    /// <summary>Phone not verified yet: the phone verification step comes first.</summary>
    PhoneUnverified,
}
