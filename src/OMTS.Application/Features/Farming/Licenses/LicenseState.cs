
namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Derived license state as of a date; never stored.</summary>
public enum LicenseState
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetEffective,
    Superseded,
    Missing,
}
