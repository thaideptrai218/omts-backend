
namespace OMTS.Infrastructure.Messaging;

public sealed class FakeSenderOptions
{
    /// <summary>Recipients (phone numbers, emails or account ids) that get a failed result, for tests.</summary>
    public List<string> FailFor { get; set; } = [];
}
