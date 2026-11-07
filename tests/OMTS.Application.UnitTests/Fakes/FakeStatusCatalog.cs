using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Constants;

namespace OMTS.Application.UnitTests.Fakes;

/// <summary>The few catalog rows the unit tests use; the real catalog lives in Infrastructure.</summary>
public sealed class FakeStatusCatalog : IStatusCatalog
{
    private static readonly Dictionary<int, (string Code, string Name)> Rows = new()
    {
        [StatusIds.Account.Active] = ("ACTIVE", "Active"),
        [StatusIds.Account.Locked] = ("LOCKED", "Locked"),
        [StatusIds.Usage.InUse] = ("IN_USE", "In Use"),
        [StatusIds.Usage.Discontinued] = ("OUT_OF_USE", "Discontinued"),
    };

    public string CodeOf(int statusId) => Rows[statusId].Code;

    public string DisplayNameOf(int statusId) => Rows[statusId].Name;
}
