
namespace OMTS.Infrastructure.Messaging;

/// <summary>"Push" section. Only the fake exists until the real push provider ticket; "None" sends nothing.</summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";
    public const string FakeProvider = "Fake";
    public const string NoneProvider = "None";

    public string Provider { get; set; } = NoneProvider;
    public FakeSenderOptions Fake { get; set; } = new();
}
