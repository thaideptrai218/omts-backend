
namespace OMTS.Application.Common.Results;

/// <summary>Result of a use case that returns nothing but may fail.</summary>
public sealed record Done
{
    public static readonly Done Value = new();
}
