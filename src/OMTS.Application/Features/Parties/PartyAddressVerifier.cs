using OMTS.Application.Features.AdministrativeUnits;

namespace OMTS.Application.Features.Parties;

/// <summary>
/// Checks the codes of a picked address against the administrative units list: the province exists and the ward belongs to it. The names are those of the
/// list. While the list cannot be read the codes and names are kept as sent, so an outage does not stop a save.
/// </summary>
public sealed class PartyAddressVerifier(AdministrativeUnitService units)
{
    public async Task<AddressParts> VerifyAsync(AddressParts address, string provinceField, string wardField, List<string> invalid, CancellationToken cancellationToken)
    {
        if (address.ProvinceCode is null)
        {
            return address;
        }

        var check = await units.CheckAsync(address.ProvinceCode, address.WardCode, cancellationToken);
        switch (check.Outcome)
        {
            case AddressCheckOutcome.UnknownProvince:
                Add(invalid, provinceField);
                return address;
            case AddressCheckOutcome.UnknownWard:
                Add(invalid, wardField);
                return address;
            case AddressCheckOutcome.Valid:
                return address with { ProvinceName = check.ProvinceName ?? address.ProvinceName, WardName = check.WardName ?? address.WardName };
            default:
                return address;
        }
    }

    private static void Add(List<string> invalid, string field)
    {
        if (!invalid.Contains(field))
        {
            invalid.Add(field);
        }
    }
}
